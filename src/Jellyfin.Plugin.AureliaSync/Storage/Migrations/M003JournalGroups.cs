using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage.Migrations;

/// <summary>
/// Adds a grouping key to journal records.
/// </summary>
/// <remarks>
/// The same rule as for snapshot rows: all of a playlist's membership records must be delivered in
/// one segment, because the client clears that playlist and reinserts only what the segment
/// contained. A change session re-materialises an entire playlist on any touch, so the journal
/// needs the same guarantee the snapshot has.
/// </remarks>
public sealed class M003JournalGroups : IMigration
{
    /// <inheritdoc />
    public int Version => 3;

    /// <inheritdoc />
    public string Name => "journal-groups";

    /// <inheritdoc />
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        SyncDatabase.Execute(connection, "ALTER TABLE journal ADD COLUMN group_key TEXT;", transaction);

        // Delivery reads a user's own records plus broadcast ones, ordered by sequence.
        SyncDatabase.Execute(
            connection,
            "CREATE INDEX IF NOT EXISTS ix_journal_scope_sequence ON journal (scope, sequence);",
            transaction);
    }
}
