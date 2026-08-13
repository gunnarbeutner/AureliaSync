using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage.Migrations;

/// <summary>
/// Adds a grouping key to snapshot rows.
/// </summary>
/// <remarks>
/// <para>
/// All of a playlist's membership records must be delivered in a single segment: the client clears
/// that playlist's membership and reinserts only the rows the segment contained, so a playlist
/// split across two segments would be truncated to whatever was in the second.
/// </para>
/// <para>
/// The segment writer therefore has to know where a group ends, and payloads are opaque bytes to
/// it. Recording the key alongside the row is cheaper than teaching the writer to parse payloads,
/// and keeps the rule enforceable rather than incidental.
/// </para>
/// </remarks>
public sealed class M002SnapshotRowGroups : IMigration
{
    /// <inheritdoc />
    public int Version => 2;

    /// <inheritdoc />
    public string Name => "snapshot-row-groups";

    /// <inheritdoc />
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        // Existing rows get NULL, which means "may be split anywhere" — correct for every record
        // kind except playlist membership.
        SyncDatabase.Execute(
            connection,
            "ALTER TABLE snapshot_rows ADD COLUMN group_key TEXT;",
            transaction);
    }
}
