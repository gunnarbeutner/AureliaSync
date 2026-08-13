using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// The append-only change journal.
/// </summary>
/// <remarks>
/// <para>
/// Records are materialised at write time, not hydrated at delivery: by the time a client asks for
/// a change the item may have changed again or been deleted, and the point of the journal is to
/// describe what happened, not what is currently true.
/// </para>
/// <para>
/// Every record is scoped to one user. A change is written once per user who can see it, using the
/// same visibility path that decided what their snapshot contained, so delivery never has to
/// re-derive visibility from a live item.
/// </para>
/// </remarks>
public sealed class JournalStore
{
    /// <summary>Scope marking a record every user receives, used for tombstones.</summary>
    public const string BroadcastScope = "broadcast";

    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    public JournalStore(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Returns the highest sequence recorded, or zero when the journal is empty.
    /// </summary>
    /// <remarks>
    /// Captured before a snapshot begins enumerating, so every change made during the build sorts
    /// strictly after the baseline and is delivered rather than lost.
    /// </remarks>
    /// <returns>The journal head.</returns>
    public long Head()
    {
        using var connection = _database.Open();
        return Scalar(connection, "SELECT COALESCE(MAX(sequence), 0) FROM journal;");
    }

    /// <summary>
    /// Returns the lowest sequence still retained, or zero when the journal is empty.
    /// </summary>
    /// <remarks>
    /// A client whose position is below this has a gap and cannot be served changes; it needs a
    /// fresh snapshot.
    /// </remarks>
    /// <returns>The journal floor.</returns>
    public long Floor()
    {
        using var connection = _database.Open();
        return Scalar(connection, "SELECT COALESCE(MIN(sequence), 0) FROM journal;");
    }

    /// <summary>
    /// Returns how many records are retained.
    /// </summary>
    /// <returns>The record count.</returns>
    public long Count()
    {
        using var connection = _database.Open();
        return Scalar(connection, "SELECT COUNT(*) FROM journal;");
    }

    /// <summary>
    /// Appends records, assigning each the next sequence.
    /// </summary>
    /// <remarks>
    /// One transaction for the batch, so a burst of related changes becomes a contiguous run of
    /// sequences rather than interleaving with another writer's.
    /// </remarks>
    /// <param name="records">Records to append, in the order they should be delivered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The highest sequence assigned, or zero when nothing was appended.</returns>
    public Task<long> AppendAsync(
        IReadOnlyList<JournalRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return Task.FromResult(0L);
        }

        return _database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO journal (scope, kind, entity_type, entity_id, wire_schema, payload,
                                         checksum, created_at, group_key)
                    VALUES ($scope, $kind, $entityType, $entityId, $schema, $payload, NULL, $now, $groupKey);
                    """;

                var scope = command.Parameters.Add("$scope", SqliteType.Text);
                var kind = command.Parameters.Add("$kind", SqliteType.Text);
                var entityType = command.Parameters.Add("$entityType", SqliteType.Text);
                var entityId = command.Parameters.Add("$entityId", SqliteType.Text);
                var schema = command.Parameters.Add("$schema", SqliteType.Integer);
                var payload = command.Parameters.Add("$payload", SqliteType.Blob);
                var now = command.Parameters.Add("$now", SqliteType.Integer);
                var groupKey = command.Parameters.Add("$groupKey", SqliteType.Text);

                now.Value = Now();
                command.Prepare();

                foreach (var record in records)
                {
                    scope.Value = record.Scope;
                    kind.Value = record.Kind;
                    entityType.Value = (object?)record.EntityType ?? DBNull.Value;
                    entityId.Value = record.EntityId;
                    schema.Value = record.WireSchema;
                    payload.Value = record.Payload;
                    groupKey.Value = (object?)record.GroupKey ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }

                using var head = connection.CreateCommand();
                head.Transaction = transaction;
                head.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM journal;";
                return Convert.ToInt64(head.ExecuteScalar(), CultureInfo.InvariantCulture);
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads records for delivery to one user.
    /// </summary>
    /// <remarks>
    /// Returns the user's own records plus broadcast ones. Bounds are checked after adding a row so
    /// that a single oversized record is still delivered rather than becoming one that can never be
    /// sent.
    /// </remarks>
    /// <param name="userId">The user being served.</param>
    /// <param name="afterSequence">Exclusive lower bound.</param>
    /// <param name="upperBound">Inclusive upper bound, fixed when the session opened.</param>
    /// <param name="maxRecords">Maximum records to return.</param>
    /// <param name="maxPayloadBytes">Approximate payload budget.</param>
    /// <returns>Records in ascending sequence order.</returns>
    public IReadOnlyList<SnapshotRow> ReadAfter(
        Guid userId,
        long afterSequence,
        long upperBound,
        int maxRecords,
        long maxPayloadBytes)
    {
        var rows = new List<SnapshotRow>(Math.Min(maxRecords, 1024));
        long bytes = 0;

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sequence, kind, entity_type, entity_id, payload, group_key
              FROM journal
             WHERE sequence > $after AND sequence <= $upper AND scope IN ($scope, $broadcast)
             ORDER BY sequence
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", afterSequence);
        command.Parameters.AddWithValue("$upper", upperBound);
        command.Parameters.AddWithValue("$scope", userId.ToString("N"));
        command.Parameters.AddWithValue("$broadcast", BroadcastScope);
        command.Parameters.AddWithValue("$limit", maxRecords);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var payload = (byte[])reader["payload"];

            // Reuses the snapshot row shape so the segment writer serves both sources unchanged.
            rows.Add(new SnapshotRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                payload,
                null,
                reader.IsDBNull(5) ? null : reader.GetString(5)));

            bytes += payload.Length;
            if (bytes >= maxPayloadBytes)
            {
                break;
            }
        }

        return rows;
    }

    /// <summary>
    /// Returns the highest sequence a user could be served, at or below a bound.
    /// </summary>
    /// <param name="userId">The user being served.</param>
    /// <param name="upperBound">Inclusive upper bound.</param>
    /// <returns>The highest visible sequence, or zero when there is nothing to send.</returns>
    public long HighestVisible(Guid userId, long upperBound)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(sequence), 0) FROM journal
             WHERE sequence <= $upper AND scope IN ($scope, $broadcast);
            """;
        command.Parameters.AddWithValue("$upper", upperBound);
        command.Parameters.AddWithValue("$scope", userId.ToString("N"));
        command.Parameters.AddWithValue("$broadcast", BroadcastScope);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reclaims records every active subscription has already consumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The floor is the lowest position any active subscription still needs, less a safety margin
    /// so that a client which has acknowledged but not yet reconnected is not stranded by a few
    /// seconds of timing.
    /// </para>
    /// <para>
    /// Deleting below a subscription's position would leave that client silently missing changes
    /// while believing it was current, so this never reclaims past one. Starving a subscription is
    /// handled separately and deliberately, by marking it rather than by deleting under it.
    /// </para>
    /// </remarks>
    /// <param name="safetyMargin">How many sequences below the minimum position to keep.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many records were removed.</returns>
    public Task<int> ReclaimAsync(long safetyMargin, CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) =>
            {
                using var lowest = connection.CreateCommand();
                lowest.Transaction = transaction;
                lowest.CommandText =
                    "SELECT COALESCE(MIN(ack_sequence), -1) FROM subscriptions WHERE state = 'active';";

                var minimum = Convert.ToInt64(lowest.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (minimum < 0)
                {
                    // No active subscription: nothing is owed to anyone, but keeping the journal is
                    // harmless and a client may yet return. Age-based cleanup handles this.
                    return 0;
                }

                var cutoff = minimum - Math.Max(0, safetyMargin);
                if (cutoff <= 0)
                {
                    return 0;
                }

                return SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    "DELETE FROM journal WHERE sequence <= $cutoff;",
                    ("$cutoff", cutoff));
            },
            cancellationToken);

    /// <summary>
    /// Removes records older than a cutoff, regardless of position.
    /// </summary>
    /// <remarks>
    /// The backstop for a journal that would otherwise grow forever because a client stopped
    /// returning. Any subscription starved by this is marked as needing a fresh snapshot.
    /// </remarks>
    /// <param name="olderThan">Age cutoff.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many records were removed.</returns>
    public Task<int> TrimOlderThanAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                "DELETE FROM journal WHERE created_at < $cutoff;",
                ("$cutoff", olderThan.ToUnixTimeMilliseconds())),
            cancellationToken);

    /// <summary>
    /// Marks every subscription whose position has fallen below the journal floor.
    /// </summary>
    /// <remarks>
    /// Marking rather than skipping is the whole point: a client that silently resumed above its
    /// gap would believe it was current while missing everything in between. Marked clients take a
    /// fresh snapshot, which is expensive but correct.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many subscriptions were marked.</returns>
    public Task<int> MarkStarvedSubscriptionsAsync(CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) =>
            {
                using var floor = connection.CreateCommand();
                floor.Transaction = transaction;
                floor.CommandText = "SELECT COALESCE(MIN(sequence), 0) FROM journal;";
                var journalFloor = Convert.ToInt64(floor.ExecuteScalar(), CultureInfo.InvariantCulture);

                if (journalFloor <= 1)
                {
                    return 0;
                }

                // A position of exactly floor - 1 is still contiguous: the next record the client
                // needs is the oldest one retained.
                return SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE subscriptions
                       SET state = 'snapshotRequired', reason = 'journalGap'
                     WHERE state = 'active' AND snapshot_acked = 1 AND ack_sequence < $floor - 1;
                    """,
                    ("$floor", journalFloor));
            },
            cancellationToken);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Callers pass compile-time constants only; no request data reaches this method.")]
    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
