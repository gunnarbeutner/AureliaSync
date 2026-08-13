namespace Jellyfin.Plugin.AureliaSync.Api;

/// <summary>
/// Machine-readable error codes returned to clients.
/// </summary>
/// <remarks>
/// These are part of the protocol contract: Aurelia branches on them to decide whether to retry,
/// open a new session, or request a fresh snapshot. Existing values must never be repurposed.
/// </remarks>
public static class SyncErrorCode
{
    /// <summary>No protocol version in common. Not recoverable without a client update.</summary>
    public const string ProtocolNotSupported = "protocolNotSupported";

    /// <summary>No wire-schema version in common. Requires a new snapshot once resolved.</summary>
    public const string SchemaNotSupported = "schemaNotSupported";

    /// <summary>The session no longer exists. Recoverable by opening a new one.</summary>
    public const string SessionExpired = "sessionExpired";

    /// <summary>The session belongs to another caller.</summary>
    public const string SessionNotOwned = "sessionNotOwned";

    /// <summary>The client's checkpoint is too old to resume from. Requires a new snapshot.</summary>
    public const string CheckpointExpired = "checkpointExpired";

    /// <summary>Journal records the client still needs have been reclaimed. Requires a new snapshot.</summary>
    public const string JournalGap = "journalGap";

    /// <summary>The snapshot is still being materialised. Retry after the indicated delay.</summary>
    public const string SnapshotPreparing = "snapshotPreparing";

    /// <summary>The snapshot was abandoned and must be rebuilt.</summary>
    public const string SnapshotInvalidated = "snapshotInvalidated";

    /// <summary>The supplied cursor is malformed or does not belong to this session.</summary>
    public const string CursorInvalid = "cursorInvalid";

    /// <summary>The acknowledged cursor was never issued to this session.</summary>
    public const string AckBeyondIssued = "ackBeyondIssued";

    /// <summary>The acknowledgement skipped a required protocol phase.</summary>
    public const string AckPhaseMismatch = "ackPhaseMismatch";

    /// <summary>Reconciliation must complete before delivery can continue.</summary>
    public const string ReconciliationRequired = "reconciliationRequired";

    /// <summary>The server is rate-limiting or at capacity. Retry later.</summary>
    public const string ServerBusy = "serverBusy";

    /// <summary>Storage limits are forcing the server to shed work.</summary>
    public const string StoragePressure = "storagePressure";

    /// <summary>The plugin's database is unavailable; synchronisation is disabled.</summary>
    public const string StorageUnavailable = "storageUnavailable";

    /// <summary>The plugin is still starting. Retry shortly.</summary>
    public const string Starting = "starting";

    /// <summary>Synchronisation is disabled by the server administrator.</summary>
    public const string Disabled = "disabled";

    /// <summary>
    /// The request authenticated without a user scope, typically via an API key. Sync endpoints
    /// operate on one user's visible library and cannot serve an unscoped caller.
    /// </summary>
    public const string UserScopeRequired = "userScopeRequired";

    /// <summary>The request was malformed.</summary>
    public const string BadRequest = "badRequest";
}
