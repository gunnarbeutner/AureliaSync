using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Api.Models;
using Jellyfin.Plugin.AureliaSync.Configuration;
using Jellyfin.Plugin.AureliaSync.Snapshots;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Streaming;
using Jellyfin.Plugin.AureliaSync.Wire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Api;

/// <summary>
/// Session lifecycle and record delivery.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AuthorizeAttribute"/> is load-bearing: Jellyfin configures a default authorization
/// policy but never a fallback policy, so a plugin controller without it is reachable anonymously.
/// </para>
/// <para>
/// <b>No endpoint here returns 404 for an expired or unknown session.</b> The client maps every 404
/// to "the plugin is not installed" before it looks at the body, so a 404 would tell a user to
/// reinstall the plugin because their session timed out. Gone (410) and Conflict (409) are used
/// instead.
/// </para>
/// </remarks>
[ApiController]
[Route("AureliaSync/v1")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class SyncSessionsController : ControllerBase
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(5);

    private readonly SyncRuntime _runtime;
    private readonly SnapshotCoordinator _coordinator;
    private readonly ILogger<SyncSessionsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncSessionsController"/> class.
    /// </summary>
    /// <param name="runtime">Shared runtime state.</param>
    /// <param name="coordinator">Snapshot builds and maintenance.</param>
    /// <param name="logger">Logger.</param>
    public SyncSessionsController(
        SyncRuntime runtime,
        SnapshotCoordinator coordinator,
        ILogger<SyncSessionsController> logger)
    {
        _runtime = runtime;
        _coordinator = coordinator;
        _logger = logger;
    }

    /// <summary>
    /// Opens or resumes a delivery session.
    /// </summary>
    /// <remarks>
    /// Returns as soon as a generation exists, without waiting for it to be built: materialising a
    /// large library takes minutes, and the client cannot hold a request open for that long.
    /// </remarks>
    /// <param name="request">Session parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session.</returns>
    [HttpPost("sessions")]
    [RequestSizeLimit(16 * 1024)]
    [ProducesResponseType(typeof(SessionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(SyncErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SessionResponse>> CreateSession(
        [FromBody] CreateSessionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        NoStore();

        if (User.GetUserId() is not { } userId)
        {
            return ApiKeyRefused();
        }

        if (!ClientIdentifier.IsValid(request.ClientId))
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                SyncErrorCode.BadRequest,
                "clientId must be 8-64 characters of letters, digits, dot, dash or underscore.");
        }

        var unavailable = await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var configuration = Configuration();

        // Version negotiation is an intersection: no overlap means the two sides cannot talk, and
        // saying so plainly is better than failing later with a decoding error.
        var protocolVersion = Math.Min(request.ProtocolMax, WireSchema.ProtocolVersionMax);
        if (protocolVersion < Math.Max(request.ProtocolMin, WireSchema.ProtocolVersionMin))
        {
            return Problem(
                StatusCodes.Status409Conflict,
                SyncErrorCode.ProtocolNotSupported,
                $"This server speaks protocol {WireSchema.ProtocolVersionMin}-{WireSchema.ProtocolVersionMax}.");
        }

        var schemaVersion = Math.Min(request.SchemaMax, WireSchema.WireSchemaVersionMax);
        if (schemaVersion < Math.Max(request.SchemaMin, WireSchema.WireSchemaVersionMin))
        {
            return Problem(
                StatusCodes.Status409Conflict,
                SyncErrorCode.SchemaNotSupported,
                $"This server emits wire schema {WireSchema.WireSchemaVersionMin}-{WireSchema.WireSchemaVersionMax}.");
        }

        var sessions = _runtime.Sessions;
        var window = DateTimeOffset.UtcNow.AddHours(-1);
        if (sessions.SessionsCreatedSince(userId, request.ClientId, window)
            >= Math.Max(1, configuration.MaxSessionsPerClientPerHour))
        {
            Response.Headers.RetryAfter = "300";
            return Problem(
                StatusCodes.Status429TooManyRequests,
                SyncErrorCode.ServerBusy,
                "Too many sessions opened recently. Try again shortly.",
                retryable: true);
        }

        // A checkpoint naming a snapshot that no longer exists is not an error: retention expired,
        // and the client did nothing wrong. It simply starts over.
        long resumeOrdinal = 0;
        long? resumeGeneration = null;

        if (!request.Reset && !string.IsNullOrEmpty(request.CheckpointToken))
        {
            var rejection = CheckpointToken.TryValidate(
                _runtime.SigningKey,
                request.CheckpointToken,
                userId,
                request.ClientId,
                out var tokenGeneration,
                out var tokenOrdinal);

            switch (rejection)
            {
                case CheckpointToken.Rejection.None:
                    resumeGeneration = tokenGeneration;
                    resumeOrdinal = tokenOrdinal;
                    break;

                case CheckpointToken.Rejection.WrongOwner:
                    return Problem(
                        StatusCodes.Status409Conflict,
                        SyncErrorCode.SessionNotOwned,
                        "That checkpoint belongs to a different user or client.");

                default:
                    return Problem(
                        StatusCodes.Status400BadRequest,
                        SyncErrorCode.CursorInvalid,
                        "The checkpoint token could not be verified.");
            }
        }

        if (request.Reset)
        {
            await sessions.ResetSubscriptionAsync(userId, request.ClientId, "clientRequested", cancellationToken)
                .ConfigureAwait(false);
        }

        // A client that already holds a fully acknowledged snapshot, and whose position is still
        // covered by the journal, gets changes instead of the whole catalog again. Anything else
        // falls back to a snapshot with a reason the client can log.
        if (!request.Reset && await TryOpenChangeSessionAsync(
                userId, request.ClientId, protocolVersion, schemaVersion, configuration, cancellationToken)
                .ConfigureAwait(false) is { } changeSession)
        {
            return StatusCode(StatusCodes.Status201Created, changeSession);
        }

        var reason = SnapshotReason(userId, request, schemaVersion);

        var snapshot = await _coordinator
            .EnsureSnapshotAsync(userId, schemaVersion, request.Reset, cancellationToken)
            .ConfigureAwait(false);

        // Resume only within the same generation. A checkpoint from an older snapshot describes
        // positions that no longer mean anything.
        var resumable = resumeGeneration == snapshot.Generation;
        var startOrdinal = resumable ? resumeOrdinal : 0;

        var session = new SessionInfo
        {
            Id = SessionStore.NewSessionId(),
            UserId = userId,
            ClientId = request.ClientId,
            Mode = "snapshot",
            ProtocolVersion = protocolVersion,
            WireSchema = schemaVersion,
            Generation = snapshot.Generation,
            BaselineSequence = snapshot.BaselineSequence,
            UpperBound = snapshot.IsStreamable ? _runtime.Snapshots.MaxOrdinal(snapshot.Generation) : 0,
            HighestIssuedOrdinal = startOrdinal,
            AckedOrdinal = startOrdinal,
            State = snapshot.IsStreamable ? SessionInfo.StateStreaming : SessionInfo.StatePreparing,
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, configuration.SessionIdleExpiryHours))
        };

        await sessions.CreateAsync(session, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "AureliaSync: opened a snapshot session for {Client} on generation {Generation} "
            + "({Reason}), resuming at {Ordinal}",
            request.ClientId,
            snapshot.Generation,
            reason,
            startOrdinal);

        return StatusCode(StatusCodes.Status201Created, Describe(session, snapshot, startOrdinal));
    }

    /// <summary>
    /// Reports a session's progress. Diagnostic; the client does not use it.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session.</returns>
    [HttpGet("sessions/{sessionId}")]
    [ProducesResponseType(typeof(SessionResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SessionResponse>> GetSession(
        string sessionId, CancellationToken cancellationToken)
    {
        NoStore();

        if (User.GetUserId() is not { } userId)
        {
            return ApiKeyRefused();
        }

        var unavailable = await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var session = _runtime.Sessions.Get(sessionId);
        if (session is null || session.UserId != userId)
        {
            return Gone();
        }

        var snapshot = session.Generation is { } generation ? _runtime.Snapshots.Get(generation) : null;
        return Ok(Describe(session, snapshot, session.AckedOrdinal));
    }

    /// <summary>
    /// Streams one bounded segment.
    /// </summary>
    /// <remarks>
    /// While a snapshot is still building this waits, then answers with a valid but empty segment
    /// rather than an error. The client has no retry path and no fallback, so any non-2xx here
    /// fails its entire sync.
    /// </remarks>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="after">Cursor to continue from.</param>
    /// <param name="maxRecords">Record limit.</param>
    /// <param name="maxBytes">Byte budget, framing included.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An NDJSON segment.</returns>
    [HttpGet("sessions/{sessionId}/stream")]
    [Produces(WireSchema.NdjsonContentType)]
    public async Task<IActionResult> Stream(
        string sessionId,
        [FromQuery] string? after,
        [FromQuery] int? maxRecords,
        [FromQuery] long? maxBytes,
        CancellationToken cancellationToken)
    {
        NoStore();

        if (User.GetUserId() is not { } userId)
        {
            return ApiKeyRefused();
        }

        var unavailable = await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return unavailable;
        }

        var session = _runtime.Sessions.Get(sessionId);
        if (session is null || session.UserId != userId)
        {
            return Gone();
        }

        if (!session.IsLive)
        {
            return Problem(
                StatusCodes.Status410Gone,
                SyncErrorCode.SessionExpired,
                "This session has expired. Open a new one; your checkpoint is preserved.",
                retryable: true);
        }

        long afterOrdinal = session.AckedOrdinal;
        if (!string.IsNullOrEmpty(after))
        {
            if (!Cursor.TryDecode(after, out var cursor))
            {
                return Problem(StatusCodes.Status400BadRequest, SyncErrorCode.CursorInvalid, "Malformed cursor.");
            }

            var isChangeSession = string.Equals(session.Mode, "changes", StringComparison.Ordinal);

            if (!isChangeSession && session.Generation is { } generation && cursor.Generation != generation)
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    SyncErrorCode.SnapshotInvalidated,
                    "That cursor belongs to a different snapshot. Open a new session.",
                    requiresSnapshot: true);
            }

            afterOrdinal = cursor.Ordinal;
        }

        var configuration = Configuration();

        if (string.Equals(session.Mode, "changes", StringComparison.Ordinal))
        {
            return await StreamChangesAsync(
                session, afterOrdinal, after, maxRecords, maxBytes, configuration, cancellationToken)
                .ConfigureAwait(false);
        }

        var snapshot = await WaitForSnapshotAsync(session, configuration, cancellationToken).ConfigureAwait(false);

        if (snapshot is { State: SnapshotInfo.StateFailed or SnapshotInfo.StateInvalidated })
        {
            return Problem(
                StatusCodes.Status409Conflict,
                SyncErrorCode.SnapshotInvalidated,
                "The snapshot was abandoned. Open a new session to rebuild it.",
                retryable: true,
                requiresSnapshot: true);
        }

        var ready = snapshot?.IsStreamable == true;
        var upperBound = ready ? _runtime.Snapshots.MaxOrdinal(snapshot!.Generation) : 0;

        if (ready && session.UpperBound != upperBound)
        {
            await _runtime.Sessions
                .AttachSnapshotAsync(sessionId, snapshot!.Generation, upperBound, cancellationToken)
                .ConfigureAwait(false);
        }

        var rows = ready
            ? _runtime.Snapshots.ReadAfter(
                snapshot!.Generation,
                afterOrdinal,
                Math.Clamp(maxRecords ?? configuration.MaxRecordsPerSegment, 1, 5000),
                Math.Clamp(maxBytes ?? configuration.MaxBytesPerSegment, 64 * 1024, 32L * 1024 * 1024))
            : Array.Empty<SnapshotRow>();

        // Everything that could fail with a status code has now been decided. Once a byte is
        // written the status is fixed and Jellyfin's exception middleware rethrows rather than
        // rewriting it, so from here on failures are reported in-band.
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = WireSchema.NdjsonContentType;
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var gzip = configuration.EnableGzip && AcceptsGzip();
        if (gzip)
        {
            Response.Headers.ContentEncoding = "gzip";
            Response.Headers.Vary = "Accept-Encoding";
        }

        Stream output = Response.Body;
        GZipStream? compressor = null;
        if (gzip)
        {
            compressor = new GZipStream(Response.Body, CompressionLevel.Fastest, leaveOpen: true);
            output = compressor;
        }

        try
        {
            var begin = new SegmentBegin
            {
                WireSchemaVersion = session.WireSchema,
                ProtocolVersion = session.ProtocolVersion,
                SessionId = session.Id,
                Mode = session.Mode,
                Generation = snapshot?.Generation ?? session.Generation,
                AfterCursor = after,
                ServerTime = DateTimeOffset.UtcNow
            };

            // The budget is the client's own limit less headroom for framing, which the client
            // counts too.
            var budget = Math.Clamp(maxBytes ?? configuration.MaxBytesPerSegment, 64 * 1024, 32L * 1024 * 1024);
            var expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, configuration.SessionIdleExpiryHours));

            SegmentOutcome outcome = default;

            outcome = await NdjsonSegmentWriter.WriteAsync(
                output,
                begin,
                rows,
                afterOrdinal,
                upperBound,
                ready,
                Math.Clamp(maxRecords ?? configuration.MaxRecordsPerSegment, 1, 5000),
                (long)(budget * 0.94),
                TimeSpan.FromMilliseconds(Math.Max(1000, configuration.SegmentTimeBudgetMs)),
                issued => _runtime.Sessions.RecordIssuedAsync(
                    sessionId,
                    issued,
                    expiresAt,
                    rows.Count,
                    rows.Sum(r => (long)r.Payload.Length),
                    CancellationToken.None),
                cancellationToken).ConfigureAwait(false);

            if (configuration.VerboseDiagnostics)
            {
                _logger.LogInformation(
                    "AureliaSync: segment for {Session} delivered {Records} records ({Bytes} bytes), caughtUp={CaughtUp}, stop={Stop}",
                    sessionId,
                    outcome.RecordCount,
                    outcome.TotalBytes,
                    outcome.CaughtUp,
                    outcome.StopReason);
            }
        }
        catch (OperationCanceledException)
        {
            // The client went away mid-segment. It will retry from its last acknowledgement, and
            // because the segment lacks its closing line nothing partial is applied.
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AureliaSync: failed while writing a segment for {Session}", sessionId);

            if (!Response.HasStarted)
            {
                throw;
            }

            await NdjsonSegmentWriter.WriteErrorAsync(
                output,
                new ErrorLine
                {
                    Code = SyncErrorCode.ServerBusy,
                    Message = "The server stopped writing this segment.",
                    CorrelationId = SyncErrorResponse.NewCorrelationId()
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (compressor is not null)
            {
                await compressor.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await compressor.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Advances the client's durable checkpoint.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="request">The acknowledgement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new checkpoint.</returns>
    [HttpPost("sessions/{sessionId}/ack")]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType(typeof(AckResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AckResponse>> Acknowledge(
        string sessionId,
        [FromBody] AckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        NoStore();

        if (User.GetUserId() is not { } userId)
        {
            return ApiKeyRefused();
        }

        var unavailable = await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return unavailable;
        }

        if (string.IsNullOrEmpty(request.ClientCommitId))
        {
            return Problem(
                StatusCodes.Status400BadRequest, SyncErrorCode.BadRequest, "clientCommitId is required.");
        }

        if (!Cursor.TryDecode(request.ThroughCursor, out var cursor))
        {
            return Problem(StatusCodes.Status400BadRequest, SyncErrorCode.CursorInvalid, "Malformed cursor.");
        }

        // The client identifier is not in the request body — it is whichever client owns the
        // session — so a replay after the session is gone still needs one. Look it up first, then
        // fall back to the session.
        var session = _runtime.Sessions.Get(sessionId);
        var clientId = session?.ClientId;

        if (session is not null && session.UserId != userId)
        {
            return Gone();
        }

        if (clientId is null)
        {
            // No session at all. Nothing can be verified, and the client is replaying blind after a
            // crash; telling it to open a new session is the honest answer.
            return Problem(
                StatusCodes.Status410Gone,
                SyncErrorCode.SessionExpired,
                "This session no longer exists. Open a new one and replay; your checkpoint is preserved.",
                retryable: true);
        }

        var snapshotRows = session?.Generation is { } generation
            ? _runtime.Snapshots.Get(generation)?.RowCount ?? 0
            : 0;

        var outcome = await _runtime.Sessions.AcknowledgeAsync(
            sessionId,
            userId,
            clientId,
            request.ClientCommitId,
            cursor.Generation,
            cursor.Ordinal,
            snapshotRows,
            cancellationToken).ConfigureAwait(false);

        switch (outcome.Result)
        {
            case AckResult.BeyondIssued:
                return Problem(
                    StatusCodes.Status409Conflict,
                    SyncErrorCode.AckBeyondIssued,
                    "That cursor was never issued to this session.");

            case AckResult.WrongGeneration:
                return Problem(
                    StatusCodes.Status409Conflict,
                    SyncErrorCode.CursorInvalid,
                    "That cursor belongs to a different snapshot.",
                    requiresSnapshot: true);

            case AckResult.SessionUnusable:
                return Problem(
                    StatusCodes.Status410Gone,
                    SyncErrorCode.SessionExpired,
                    "This session has expired. Open a new one; your checkpoint is preserved.",
                    retryable: true);
        }

        // A change session's position is a journal sequence, and it is the subscription — not the
        // session — that must remember it: the session is disposable, the position is not.
        if (session is not null && string.Equals(session.Mode, "changes", StringComparison.Ordinal))
        {
            await _runtime.Sessions
                .AdvanceJournalPositionAsync(userId, clientId, outcome.AckedOrdinal, cancellationToken)
                .ConfigureAwait(false);
        }

        var token = CheckpointToken.Issue(
            _runtime.SigningKey, userId, clientId, cursor.Generation, outcome.AckedOrdinal);

        return Ok(new AckResponse
        {
            CheckpointToken = token,
            AckedCursor = Cursor.ForSnapshot(cursor.Generation, outcome.AckedOrdinal).Encode(),
            SnapshotComplete = outcome.SnapshotComplete
        });
    }

    /// <summary>
    /// Closes a session, preserving the client's checkpoint.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("sessions/{sessionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> CloseSession(string sessionId, CancellationToken cancellationToken)
    {
        NoStore();

        if (User.GetUserId() is not { } userId)
        {
            return ApiKeyRefused();
        }

        // Deliberately unconditional success. The client fires this from a deferred task, so it
        // races the final acknowledgement and is also sent for sessions that already failed;
        // reporting an error for an already-closed session would surface a spurious failure.
        if (_runtime.IsUsable)
        {
            await _runtime.Sessions.CloseAsync(sessionId, userId, cancellationToken).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Abandons a client's checkpoint and requires a fresh snapshot.
    /// </summary>
    /// <remarks>
    /// Does not delete anything the client holds: it keeps serving its existing library until a
    /// replacement is fully committed.
    /// </remarks>
    /// <param name="request">Which client to reset.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("subscription/reset")]
    [RequestSizeLimit(4 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ResetSubscription(
        [FromBody] CreateSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        NoStore();

        if (User.GetUserId() is not { } userId)
        {
            return ApiKeyRefused();
        }

        if (!ClientIdentifier.IsValid(request.ClientId))
        {
            return Problem(StatusCodes.Status400BadRequest, SyncErrorCode.BadRequest, "clientId is required.");
        }

        var unavailable = await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return unavailable;
        }

        await _runtime.Sessions
            .ResetSubscriptionAsync(userId, request.ClientId, "clientRequested", cancellationToken)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Works out why this client is being given a snapshot rather than changes.
    /// </summary>
    /// <remarks>
    /// Reported to the client so a full resynchronisation is never simply unexplained. Each of these
    /// costs the client its whole catalog, so which one happened is the difference between
    /// diagnosing a problem and guessing at it.
    /// </remarks>
    private string SnapshotReason(Guid userId, CreateSessionRequest request, int schemaVersion)
    {
        if (request.Reset)
        {
            return SessionReason.ClientRequested;
        }

        var subscription = _runtime.Sessions.GetSubscription(userId, request.ClientId);
        if (subscription is null)
        {
            return SessionReason.NewClient;
        }

        if (string.Equals(subscription.State, SubscriptionInfo.StateExpired, StringComparison.Ordinal))
        {
            return SessionReason.CheckpointExpired;
        }

        // A gap is recorded by whichever pass discovered it — retention, or session creation — so
        // the reason survives even though the subscription has since been reset.
        if (string.Equals(subscription.Reason, SyncErrorCode.JournalGap, StringComparison.Ordinal))
        {
            return SessionReason.JournalGap;
        }

        if (!subscription.SnapshotAcked)
        {
            return SessionReason.SnapshotIncomplete;
        }

        return subscription.Reason ?? SessionReason.SchemaChanged;
    }

    /// <summary>
    /// Streams one segment of journal records.
    /// </summary>
    /// <remarks>
    /// Reuses the same segment writer as snapshot delivery; only the row source differs, which is
    /// what keeps the two paths from diverging in framing, limits or grouping rules.
    /// </remarks>
    private async Task<IActionResult> StreamChangesAsync(
        SessionInfo session,
        long afterSequence,
        string? after,
        int? maxRecords,
        long? maxBytes,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var journal = _runtime.Journal;
        var records = Math.Clamp(maxRecords ?? configuration.MaxRecordsPerSegment, 1, 5000);
        var budget = Math.Clamp(maxBytes ?? configuration.MaxBytesPerSegment, 64 * 1024, 32L * 1024 * 1024);

        var rows = journal.ReadAfter(session.UserId, afterSequence, session.UpperBound, records, budget);

        // Catch-up is measured against what this user could actually be sent, not the global head:
        // another user's records sit between these sequences and would otherwise leave the client
        // waiting forever for records it can never receive.
        var highestVisible = journal.HighestVisible(session.UserId, session.UpperBound);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = WireSchema.NdjsonContentType;
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var gzip = configuration.EnableGzip && AcceptsGzip();
        if (gzip)
        {
            Response.Headers.ContentEncoding = "gzip";
            Response.Headers.Vary = "Accept-Encoding";
        }

        Stream output = Response.Body;
        GZipStream? compressor = null;
        if (gzip)
        {
            compressor = new GZipStream(Response.Body, CompressionLevel.Fastest, leaveOpen: true);
            output = compressor;
        }

        try
        {
            var begin = new SegmentBegin
            {
                WireSchemaVersion = session.WireSchema,
                ProtocolVersion = session.ProtocolVersion,
                SessionId = session.Id,
                Mode = session.Mode,
                Generation = session.Generation,
                AfterCursor = after,
                ServerTime = DateTimeOffset.UtcNow
            };

            var expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, configuration.SessionIdleExpiryHours));

            await NdjsonSegmentWriter.WriteAsync(
                output,
                begin,
                rows,
                afterSequence,
                highestVisible,
                snapshotReady: true,
                records,
                (long)(budget * 0.94),
                TimeSpan.FromMilliseconds(Math.Max(1000, configuration.SegmentTimeBudgetMs)),
                issued => _runtime.Sessions.RecordIssuedAsync(
                    session.Id,
                    issued,
                    expiresAt,
                    rows.Count,
                    rows.Sum(r => (long)r.Payload.Length),
                    CancellationToken.None),
                cancellationToken,
                sequence => new Cursor(Cursor.JournalKind, 0, sequence)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AureliaSync: failed while writing a change segment for {Session}", session.Id);

            if (!Response.HasStarted)
            {
                throw;
            }

            await NdjsonSegmentWriter.WriteErrorAsync(
                output,
                new ErrorLine
                {
                    Code = SyncErrorCode.ServerBusy,
                    Message = "The server stopped writing this segment.",
                    CorrelationId = SyncErrorResponse.NewCorrelationId()
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (compressor is not null)
            {
                await compressor.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                await compressor.DisposeAsync().ConfigureAwait(false);
            }
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Opens a change session, or returns null when the client must take a snapshot instead.
    /// </summary>
    /// <remarks>
    /// The gap check is the important part. A client whose position has fallen below the journal
    /// floor cannot simply resume from the floor: it would skip everything in between while
    /// believing it was current. It takes a fresh snapshot instead, which is expensive and correct.
    /// </remarks>
    private async Task<SessionResponse?> TryOpenChangeSessionAsync(
        Guid userId,
        string clientId,
        int protocolVersion,
        int schemaVersion,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var subscription = _runtime.Sessions.GetSubscription(userId, clientId);
        if (subscription is null || !subscription.CanReceiveChanges)
        {
            return null;
        }

        var journal = _runtime.Journal;
        var floor = journal.Floor();

        // floor - 1 is still contiguous: the next record the client needs is the oldest retained.
        if (floor > 0 && subscription.AckSequence < floor - 1)
        {
            await _runtime.Sessions
                .ResetSubscriptionAsync(userId, clientId, SyncErrorCode.JournalGap, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "AureliaSync: client {Client} is at {Position} but the journal starts at {Floor}; a fresh snapshot is required",
                clientId,
                subscription.AckSequence,
                floor);
            return null;
        }

        // Fixed when the session opens, so records written while it drains do not move the finish
        // line and prevent it ever catching up.
        var head = journal.Head();

        var session = new SessionInfo
        {
            Id = SessionStore.NewSessionId(),
            UserId = userId,
            ClientId = clientId,
            Mode = "changes",
            ProtocolVersion = protocolVersion,
            WireSchema = schemaVersion,
            Generation = subscription.SnapshotGeneration,
            BaselineSequence = subscription.AckSequence,
            UpperBound = head,
            HighestIssuedOrdinal = subscription.AckSequence,
            AckedOrdinal = subscription.AckSequence,
            State = SessionInfo.StateStreaming,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, configuration.SessionIdleExpiryHours))
        };

        await _runtime.Sessions.CreateAsync(session, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "AureliaSync: opened a change session for {Client} from sequence {From} to {To}",
            clientId,
            subscription.AckSequence,
            head);

        return new SessionResponse
        {
            SessionId = session.Id,
            Mode = "changes",
            ProtocolVersion = protocolVersion,
            SchemaVersion = schemaVersion,
            Cursor = subscription.AckSequence > 0
                ? new Cursor(Cursor.JournalKind, 0, subscription.AckSequence).Encode()
                : null,
            CheckpointToken = CheckpointToken.Issue(
                _runtime.SigningKey, userId, clientId, 0, subscription.AckSequence),
            SnapshotGeneration = subscription.SnapshotGeneration?.ToString(CultureInfo.InvariantCulture),
            JournalHead = head,
            ExpiresAt = session.ExpiresAt,
            State = session.State
        };
    }

    /// <summary>
    /// Waits for a snapshot to finish building, bounded well below the client's timeout.
    /// </summary>
    private async Task<SnapshotInfo?> WaitForSnapshotAsync(
        SessionInfo session, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (session.Generation is not { } generation)
        {
            return null;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(configuration.StreamWaitSeconds, 0, 45));

        while (true)
        {
            var snapshot = _runtime.Snapshots.Get(generation);
            if (snapshot is null
                || snapshot.IsStreamable
                || snapshot.State is SnapshotInfo.StateFailed or SnapshotInfo.StateInvalidated
                || DateTimeOffset.UtcNow >= deadline)
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    private SessionResponse Describe(SessionInfo session, SnapshotInfo? snapshot, long ordinal) =>
        new SessionResponse
        {
            SessionId = session.Id,
            Mode = session.Mode,
            ProtocolVersion = session.ProtocolVersion,
            SchemaVersion = session.WireSchema,
            Cursor = ordinal > 0 && session.Generation is { } generation
                ? Cursor.ForSnapshot(generation, ordinal).Encode()
                : null,
            CheckpointToken = ordinal > 0 && session.Generation is { } tokenGeneration
                ? CheckpointToken.Issue(_runtime.SigningKey, session.UserId, session.ClientId, tokenGeneration, ordinal)
                : null,
            SnapshotGeneration = session.Generation?.ToString(CultureInfo.InvariantCulture),
            JournalHead = 0,
            ExpiresAt = session.ExpiresAt,
            State = session.State,
            Reason = session.Reason,
            Message = snapshot is { State: SnapshotInfo.StateBuilding }
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "Preparing the library snapshot ({0} of {1} {2}).",
                    snapshot.PhaseDone,
                    snapshot.PhaseTotal,
                    snapshot.Phase ?? "items")
                : null
        };

    private bool AcceptsGzip() =>
        Request.Headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase);

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private static PluginConfiguration Configuration() =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private async Task<ObjectResult?> EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        var health = await _runtime.WaitAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);

        if (!Configuration().Enabled)
        {
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                SyncErrorCode.Disabled,
                "Synchronisation is disabled by the server administrator.");
        }

        if (health == SyncDatabaseHealth.Starting)
        {
            Response.Headers.RetryAfter = "5";
            return Problem(
                StatusCodes.Status503ServiceUnavailable,
                SyncErrorCode.Starting,
                "The plugin is still starting.",
                retryable: true);
        }

        return _runtime.IsUsable
            ? null
            : Problem(
                StatusCodes.Status503ServiceUnavailable,
                SyncErrorCode.StorageUnavailable,
                _runtime.Diagnostic ?? "The plugin database is unavailable.");
    }

    private ObjectResult ApiKeyRefused() => Problem(
        StatusCodes.Status403Forbidden,
        SyncErrorCode.UserScopeRequired,
        "AureliaSync requires a user-scoped access token. API key authentication carries no user scope.");

    /// <summary>
    /// An expired or unknown session. Never 404, which the client reads as "plugin not installed".
    /// </summary>
    private ObjectResult Gone() => Problem(
        StatusCodes.Status410Gone,
        SyncErrorCode.SessionExpired,
        "This session no longer exists. Open a new one; your checkpoint is preserved.",
        retryable: true);

    private ObjectResult Problem(
        int status, string code, string message, bool retryable = false, bool requiresSnapshot = false)
    {
        var body = SyncErrorResponse.Create(code, message, retryable, requiresSnapshot);
        _logger.LogDebug(
            "AureliaSync: {Status} {Code} ({Correlation})", status, code, body.Error.CorrelationId);
        return StatusCode(status, body);
    }
}
