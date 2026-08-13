namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Why a client was given a snapshot rather than changes.
/// </summary>
/// <remarks>
/// Reported to the client so that a full resynchronisation is never simply unexplained. Each of
/// these costs the client the whole catalog, so knowing which one happened is the difference
/// between diagnosing a problem and guessing at it.
/// </remarks>
public static class SessionReason
{
    /// <summary>No checkpoint on record; this client has never synchronised.</summary>
    public const string NewClient = "newClient";

    /// <summary>The client was away long enough that its position was discarded.</summary>
    public const string CheckpointExpired = "checkpointExpired";

    /// <summary>Records the client still needed had already been reclaimed.</summary>
    public const string JournalGap = "journalGap";

    /// <summary>The negotiated wire schema differs from the one the client last used.</summary>
    public const string SchemaChanged = "schemaChanged";

    /// <summary>The client asked for a fresh snapshot.</summary>
    public const string ClientRequested = "clientRequested";

    /// <summary>The client's previous snapshot was never fully acknowledged.</summary>
    public const string SnapshotIncomplete = "snapshotIncomplete";
}
