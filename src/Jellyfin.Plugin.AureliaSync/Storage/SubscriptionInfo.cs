namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// A client's durable position.
/// </summary>
/// <remarks>
/// This is the state that must outlive sessions, restarts and plugin upgrades. Losing it costs the
/// client a full resynchronisation.
/// </remarks>
public sealed record SubscriptionInfo
{
    /// <summary>The client is current and can be served changes.</summary>
    public const string StateActive = "active";

    /// <summary>The client must take a fresh snapshot before it can be served changes.</summary>
    public const string StateSnapshotRequired = "snapshotRequired";

    /// <summary>The client has been away too long and its position has been discarded.</summary>
    public const string StateExpired = "expired";

    /// <summary>Gets the journal sequence through which this client has durably committed.</summary>
    public long AckSequence { get; init; }

    /// <summary>Gets the snapshot the client last promoted.</summary>
    public long? SnapshotGeneration { get; init; }

    /// <summary>Gets a value indicating whether that snapshot was fully acknowledged.</summary>
    public bool SnapshotAcked { get; init; }

    /// <summary>Gets the subscription state.</summary>
    public string State { get; init; } = StateActive;

    /// <summary>Gets why a fresh snapshot is required, when one is.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Gets a value indicating whether this client can be served changes rather than a snapshot.
    /// </summary>
    /// <remarks>
    /// Requires a fully acknowledged snapshot: a client that promoted only part of one has no
    /// coherent catalog for deltas to build on.
    /// </remarks>
    public bool CanReceiveChanges =>
        SnapshotAcked && string.Equals(State, StateActive, System.StringComparison.Ordinal);
}
