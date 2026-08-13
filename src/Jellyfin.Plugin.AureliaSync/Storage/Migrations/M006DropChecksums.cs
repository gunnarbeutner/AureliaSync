using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage.Migrations;

/// <summary>
/// Drops the digest columns, which protected nothing.
/// </summary>
/// <remarks>
/// <para>
/// Two digests were kept and neither earned its place. The per-segment one was computed over the
/// bytes being sent and verified against the bytes received, so it could only ever detect corruption
/// strictly between those two points — a window already covered by the <c>segment.end</c> framing
/// (a segment missing its closing line is discarded whole), by gzip's own CRC when compression is
/// negotiated, and by TLS. It could never detect a server-side mistake, because it was computed from
/// the same wrong bytes it would have had to catch.
/// </para>
/// <para>
/// The per-snapshot one was worse: producing it re-read every row of the finished snapshot, and no
/// code path ever read the result. It was genuinely useful exactly once, as an oracle proving that
/// skipping Jellyfin's redundant deserialisation produced identical output — a testing affordance,
/// not a runtime guarantee, and not worth paying for on every build.
/// </para>
/// <para>
/// The per-record column was never populated at all.
/// </para>
/// </remarks>
public sealed class M006DropChecksums : IMigration
{
    /// <inheritdoc />
    public int Version => 6;

    /// <inheritdoc />
    public string Name => "drop-checksums";

    /// <inheritdoc />
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var table in new[] { "snapshots", "snapshot_rows", "journal" })
        {
            SyncDatabase.Execute(connection, $"ALTER TABLE {table} DROP COLUMN checksum;", transaction);
        }
    }
}
