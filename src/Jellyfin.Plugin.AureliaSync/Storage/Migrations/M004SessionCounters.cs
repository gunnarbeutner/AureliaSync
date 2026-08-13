using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage.Migrations;

/// <summary>
/// Adds delivery counters to sessions.
/// </summary>
/// <remarks>
/// A client that gives up part-way through a sync currently leaves almost nothing behind: the
/// session records what state it reached but not what it actually delivered, so working out whether
/// the server sent too little, too much, or nothing at all means reading the server log. These
/// columns make a failed sync legible from the session row alone, which matters most when the other
/// half of the conversation is an app on someone else's phone.
/// </remarks>
public sealed class M004SessionCounters : IMigration
{
    /// <inheritdoc />
    public int Version => 4;

    /// <inheritdoc />
    public string Name => "session-counters";

    /// <inheritdoc />
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var column in new[]
                 {
                     "segments_delivered INTEGER NOT NULL DEFAULT 0",
                     "records_delivered INTEGER NOT NULL DEFAULT 0",
                     "bytes_delivered INTEGER NOT NULL DEFAULT 0",
                     "last_error_correlation TEXT",
                     "reason TEXT"
                 })
        {
            SyncDatabase.Execute(connection, $"ALTER TABLE sessions ADD COLUMN {column};", transaction);
        }
    }
}
