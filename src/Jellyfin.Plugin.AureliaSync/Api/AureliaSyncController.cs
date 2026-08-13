using System;
using System.Globalization;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Api.Models;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Wire;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Api;

/// <summary>
/// The AureliaSync HTTP API.
/// </summary>
/// <remarks>
/// <para>
/// The class-level <see cref="AuthorizeAttribute"/> is load-bearing. Jellyfin's pipeline configures
/// a default authorization policy but never a fallback policy, and adds no global authorization
/// filter, so a plugin controller without <c>[Authorize]</c> is reachable anonymously. Removing it
/// would expose the whole library to unauthenticated callers.
/// </para>
/// <para>
/// User identity is taken exclusively from the authenticated principal. No endpoint accepts a user
/// identifier, server URL, filesystem path, or type name from a request.
/// </para>
/// </remarks>
[ApiController]
[Route("AureliaSync/v1")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
public class AureliaSyncController : ControllerBase
{
    /// <summary>
    /// How long a request waits for a still-initialising database before giving up.
    /// </summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);

    private readonly SyncRuntime _runtime;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<AureliaSyncController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AureliaSyncController"/> class.
    /// </summary>
    /// <param name="runtime">Shared runtime state.</param>
    /// <param name="applicationHost">Jellyfin application host.</param>
    /// <param name="logger">Logger.</param>
    public AureliaSyncController(
        SyncRuntime runtime,
        IServerApplicationHost applicationHost,
        ILogger<AureliaSyncController> logger)
    {
        _runtime = runtime;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>
    /// Reports plugin capability and health, and whether the calling user needs a fresh snapshot.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status document.</returns>
    [HttpGet("status")]
    [ProducesResponseType(typeof(StatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(SyncErrorResponse), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StatusResponse>> GetStatus(CancellationToken cancellationToken)
    {
        var correlationId = SyncErrorResponse.NewCorrelationId();

        var userId = User.GetUserId();
        if (userId is null)
        {
            // An API key authenticates the caller but binds them to no user, so there is no library
            // scope and no user data we could safely serve.
            return StatusCode(
                StatusCodes.Status403Forbidden,
                SyncErrorResponse.Create(
                    SyncErrorCode.UserScopeRequired,
                    "AureliaSync requires a user-scoped access token. API key authentication carries no user scope.",
                    correlationId: correlationId));
        }

        var configuration = Plugin.Instance?.Configuration;
        var health = await _runtime.WaitAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);

        var response = new StatusResponse
        {
            PluginVersion = Plugin.Instance?.Version?.ToString() ?? "0.0.0.0",
            ProtocolVersions = new VersionRange
            {
                Min = WireSchema.ProtocolVersionMin,
                Max = WireSchema.ProtocolVersionMax
            },
            WireSchemaVersions = new VersionRange
            {
                Min = WireSchema.WireSchemaVersionMin,
                Max = WireSchema.WireSchemaVersionMax
            },
            ServerVersion = _applicationHost.ApplicationVersionString,
            Health = DescribeHealth(health),
            HealthDetail = _runtime.Diagnostic,
            Enabled = configuration?.Enabled ?? false,
            DatabaseSchemaVersion = _runtime.SchemaVersion,
            User = new UserStatus { Id = userId.Value.ToString("N") },
            Limits = new LimitsStatus
            {
                MaxRecordsPerSegment = configuration?.MaxRecordsPerSegment ?? 1000,
                MaxBytesPerSegment = configuration?.MaxBytesPerSegment ?? (8 * 1024 * 1024),
                SegmentTimeBudgetMs = configuration?.SegmentTimeBudgetMs ?? 10_000
            },
            ServerTime = DateTimeOffset.UtcNow,
            CorrelationId = correlationId
        };

        if (_runtime.IsUsable)
        {
            PopulateFromDatabase(response, userId.Value);
        }

        return Ok(response);
    }

    /// <summary>
    /// Reports administrator-facing diagnostics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A diagnostics document.</returns>
    [HttpGet("admin/diagnostics")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<object>> GetDiagnostics(CancellationToken cancellationToken)
    {
        var health = await _runtime.WaitAsync(ReadyTimeout, cancellationToken).ConfigureAwait(false);

        var counts = new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);
        string? databasePath = null;

        if (_runtime.IsUsable)
        {
            databasePath = _runtime.Database.DatabasePath;
            using var connection = _runtime.Database.Open();

            // Constant statements rather than an interpolated table name: nothing here is derived
            // from the request, and keeping the SQL literal keeps it that way by construction.
            counts["snapshots"] = Scalar(connection, "SELECT COUNT(*) FROM snapshots;");
            counts["snapshot_rows"] = Scalar(connection, "SELECT COUNT(*) FROM snapshot_rows;");
            counts["sessions"] = Scalar(connection, "SELECT COUNT(*) FROM sessions;");
            counts["subscriptions"] = Scalar(connection, "SELECT COUNT(*) FROM subscriptions;");
            counts["journal"] = Scalar(connection, "SELECT COUNT(*) FROM journal;");
        }

        // Deliberately reports aggregates only: no payloads, and no other user's listening data.
        return Ok(new
        {
            health = DescribeHealth(health),
            healthDetail = _runtime.Diagnostic,
            databasePath,
            databaseSchemaVersion = _runtime.SchemaVersion,
            pluginVersion = Plugin.Instance?.Version?.ToString() ?? "0.0.0.0",
            serverVersion = _applicationHost.ApplicationVersionString,
            targetAbi = WireSchema.TargetAbi,
            rowCounts = counts,
            serverTime = DateTimeOffset.UtcNow
        });
    }

    private static string DescribeHealth(SyncDatabaseHealth health) => health switch
    {
        SyncDatabaseHealth.Ok => "ok",
        SyncDatabaseHealth.Degraded => "degraded",
        SyncDatabaseHealth.Unavailable => "unavailable",
        _ => "starting"
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Callers pass compile-time constants only; no request data reaches this method.")]
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null || value is DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private void PopulateFromDatabase(StatusResponse response, Guid userId)
    {
        var scope = userId.ToString("N");

        try
        {
            using var connection = _runtime.Database.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT COALESCE(MAX(sequence), 0), COALESCE(MIN(sequence), 0), COUNT(*) FROM journal;";
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    response.Journal.Head = reader.GetInt64(0);
                    response.Journal.Floor = reader.GetInt64(1);
                    response.Journal.Records = reader.GetInt64(2);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT generation, state, row_count, phase
                      FROM snapshots
                     WHERE user_id = $user
                     ORDER BY generation DESC
                     LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$user", scope);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    response.Snapshot.Generation = reader.GetInt64(0);
                    response.Snapshot.State = reader.GetString(1);
                    response.Snapshot.RowCount = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                    response.Snapshot.Phase = reader.IsDBNull(3) ? null : reader.GetString(3);
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT COUNT(*)
                      FROM subscriptions
                     WHERE user_id = $user
                       AND state = 'active'
                       AND snapshot_acked = 1;
                    """;
                command.Parameters.AddWithValue("$user", scope);
                var active = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

                response.User.HasCheckpoint = active > 0;

                // Phase 2 has no change sessions yet, so every client still needs a snapshot; from
                // phase 3 this becomes a per-client question answered at session creation.
                response.User.NeedsSnapshot = true;
            }
        }
        catch (SqliteException ex)
        {
            // Status must stay cheap and must never fail the probe: a client that cannot read status
            // falls back to the stock Jellyfin API, which is a worse outcome than a partial answer.
            _logger.LogWarning(ex, "AureliaSync: could not read status detail from the plugin database");
            response.HealthDetail ??= "Status detail unavailable; see server log.";
        }
    }
}
