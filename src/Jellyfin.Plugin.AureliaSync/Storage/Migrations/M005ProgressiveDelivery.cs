using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage.Migrations;

/// <summary>
/// Adds the watermark that lets a snapshot be streamed while it is still being built.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot used to be all-or-nothing: nothing could be delivered until the whole library had been
/// materialised, which on a 30,000-track library meant a client waited roughly two minutes receiving
/// correctly framed but empty segments. The rows were being produced the whole time; they simply
/// could not be handed out.
/// </para>
/// <para>
/// <c>streamable_through</c> is the highest ordinal that is safe to deliver. The builder now writes
/// in strictly ascending ordinal order and advances this after each batch, so everything at or below
/// it is final and everything above it may not exist yet.
/// </para>
/// </remarks>
public sealed class M005ProgressiveDelivery : IMigration
{
    /// <inheritdoc />
    public int Version => 5;

    /// <inheritdoc />
    public string Name => "progressive-delivery";

    /// <inheritdoc />
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        SyncDatabase.Execute(
            connection,
            "ALTER TABLE snapshots ADD COLUMN streamable_through INTEGER NOT NULL DEFAULT 0;",
            transaction);

        // Non-null marks this build as a repair: only items saved at or after this instant are
        // projected, and a manifest of surviving identifiers is emitted so the client can prune what
        // was deleted while it was away.
        SyncDatabase.Execute(
            connection,
            "ALTER TABLE snapshots ADD COLUMN repair_since INTEGER;",
            transaction);

        // Snapshots that finished under the old scheme are wholly deliverable, and their ordinal
        // layout differs from the new one. Publishing their true maximum keeps them streamable
        // rather than stranding clients mid-catalog behind a watermark of zero.
        SyncDatabase.Execute(
            connection,
            """
            UPDATE snapshots
               SET streamable_through = COALESCE(
                     (SELECT MAX(ordinal) FROM snapshot_rows WHERE snapshot_rows.generation = snapshots.generation),
                     0)
             WHERE state = 'complete';
            """,
            transaction);
    }
}
