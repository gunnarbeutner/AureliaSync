namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Health of the plugin's own database, surfaced by the status endpoint.
/// </summary>
public enum SyncDatabaseHealth
{
    /// <summary>
    /// The database has not finished opening yet. Requests should be retried shortly.
    /// </summary>
    Starting = 0,

    /// <summary>
    /// The database is open and at the expected schema version.
    /// </summary>
    Ok = 1,

    /// <summary>
    /// The database is usable but something needs administrator attention.
    /// </summary>
    Degraded = 2,

    /// <summary>
    /// The database could not be opened or migrated. All sync endpoints refuse.
    /// </summary>
    Unavailable = 3
}
