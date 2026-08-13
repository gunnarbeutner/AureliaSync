using System;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// A delivery session: a resumable, bounded view over durable state.
/// </summary>
/// <remarks>
/// A session is not the checkpoint. Losing one must never lose a client's position, which lives in
/// the subscription row and is handed back as a signed checkpoint token.
/// </remarks>
public sealed record SessionInfo
{
    /// <summary>A session whose snapshot is still being materialised.</summary>
    public const string StatePreparing = "preparing";

    /// <summary>A session actively delivering records.</summary>
    public const string StateStreaming = "streaming";

    /// <summary>A session whose snapshot has been fully acknowledged.</summary>
    public const string StateSnapshotComplete = "snapshotComplete";

    /// <summary>A session the client closed.</summary>
    public const string StateClosed = "closed";

    /// <summary>A session that went quiet for too long.</summary>
    public const string StateExpired = "expired";

    /// <summary>Gets the opaque session identifier. Appears in a URL path, so it is path-safe.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the owning user.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets the owning client installation.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Gets the delivery mode. Always <c>snapshot</c> in protocol v1.</summary>
    public string Mode { get; init; } = "snapshot";

    /// <summary>Gets the negotiated protocol version.</summary>
    public int ProtocolVersion { get; init; }

    /// <summary>Gets the negotiated wire schema version.</summary>
    public int WireSchema { get; init; }

    /// <summary>Gets the snapshot being delivered.</summary>
    public long? Generation { get; init; }

    /// <summary>Gets the journal position the snapshot was taken at.</summary>
    public long BaselineSequence { get; init; }

    /// <summary>Gets the highest ordinal this session will deliver.</summary>
    public long UpperBound { get; init; }

    /// <summary>
    /// Gets the highest ordinal actually handed to the client.
    /// </summary>
    /// <remarks>
    /// Recorded before the closing line of a segment is written, so an acknowledgement for a cursor
    /// the client received always validates even if the connection died during the final flush.
    /// </remarks>
    public long HighestIssuedOrdinal { get; init; }

    /// <summary>Gets the highest ordinal the client has durably committed.</summary>
    public long AckedOrdinal { get; init; }

    /// <summary>Gets the session state.</summary>
    public string State { get; init; } = StatePreparing;

    /// <summary>
    /// Gets why this session is a snapshot rather than a change session.
    /// </summary>
    /// <remarks>
    /// Machine-readable, so a client log line explains itself rather than saying only that a full
    /// resynchronisation happened: <c>newClient</c>, <c>checkpointExpired</c>, <c>journalGap</c>,
    /// <c>schemaChanged</c>, or <c>clientRequested</c>.
    /// </remarks>
    public string? Reason { get; init; }

    /// <summary>Gets how many segments this session has delivered.</summary>
    public long SegmentsDelivered { get; init; }

    /// <summary>Gets how many records this session has delivered.</summary>
    public long RecordsDelivered { get; init; }

    /// <summary>Gets how many payload bytes this session has delivered.</summary>
    public long BytesDelivered { get; init; }

    /// <summary>Gets the correlation identifier of this session's most recent failure.</summary>
    public string? LastErrorCorrelation { get; init; }

    /// <summary>Gets when the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets when the session was last used.</summary>
    public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>Gets when the session expires if left idle.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Gets a value indicating whether the session can still be used.</summary>
    public bool IsLive =>
        State is StatePreparing or StateStreaming or StateSnapshotComplete
        && ExpiresAt > DateTimeOffset.UtcNow;
}
