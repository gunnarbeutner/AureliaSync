using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Persists materialised snapshots and reads them back for delivery.
/// </summary>
public sealed class SnapshotStore
{
    private const string SelectColumns =
        """
        SELECT generation, user_id, state, baseline_sequence, wire_schema, row_count, byte_count,
               phase, phase_done, phase_total, error_code, error_detail,
               created_at, completed_at, expires_at, streamable_through, repair_since
          FROM snapshots
        """;

    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    public SnapshotStore(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Starts a new snapshot in the <c>building</c> state.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="wireSchema">Wire schema the payloads will be written for.</param>
    /// <param name="baselineSequence">Journal position at the moment the snapshot begins.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new generation.</returns>
    public Task<long> CreateAsync(
        Guid userId,
        int wireSchema,
        long baselineSequence,
        CancellationToken cancellationToken = default) =>
        CreateAsync(userId, wireSchema, baselineSequence, null, cancellationToken);

    /// <summary>
    /// Starts a new snapshot, optionally as a repair.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="wireSchema">Wire schema the payloads will be written for.</param>
    /// <param name="baselineSequence">Journal position at the moment the snapshot begins.</param>
    /// <param name="repairSince">
    /// When set, build a repair covering changes from this instant rather than a full snapshot.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new generation.</returns>
    public Task<long> CreateAsync(
        Guid userId,
        int wireSchema,
        long baselineSequence,
        DateTimeOffset? repairSince,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO snapshots (user_id, state, baseline_sequence, wire_schema, created_at, repair_since)
                    VALUES ($user, 'building', $baseline, $schema, $now, $repair);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$user", userId.ToString("N"));
                command.Parameters.AddWithValue("$baseline", baselineSequence);
                command.Parameters.AddWithValue("$schema", wireSchema);
                command.Parameters.AddWithValue("$now", Now());
                command.Parameters.AddWithValue(
                    "$repair",
                    repairSince is { } since ? since.ToUnixTimeMilliseconds() : DBNull.Value);

                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            },
            cancellationToken);

    /// <summary>
    /// Appends a batch of rows.
    /// </summary>
    /// <remarks>
    /// One transaction per batch, with a single prepared statement reused across the batch. Holding
    /// one transaction open for a whole 34,500-row build would block every reader for minutes and
    /// grow the write-ahead log to the size of the snapshot.
    /// </remarks>
    /// <param name="generation">Target snapshot.</param>
    /// <param name="rows">Rows to append, in ascending ordinal order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the batch is committed.</returns>
    public Task AppendAsync(
        long generation,
        IReadOnlyList<SnapshotRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO snapshot_rows (generation, ordinal, kind, entity_type, entity_id, payload, group_key)
                    VALUES ($generation, $ordinal, $kind, $entityType, $entityId, $payload, $groupKey);
                    """;

                var generationParameter = command.Parameters.Add("$generation", SqliteType.Integer);
                var ordinal = command.Parameters.Add("$ordinal", SqliteType.Integer);
                var kind = command.Parameters.Add("$kind", SqliteType.Text);
                var entityType = command.Parameters.Add("$entityType", SqliteType.Text);
                var entityId = command.Parameters.Add("$entityId", SqliteType.Text);
                var payload = command.Parameters.Add("$payload", SqliteType.Blob);
                var groupKey = command.Parameters.Add("$groupKey", SqliteType.Text);

                generationParameter.Value = generation;
                command.Prepare();

                foreach (var row in rows)
                {
                    ordinal.Value = row.Ordinal;
                    kind.Value = row.Kind;
                    entityType.Value = (object?)row.EntityType ?? DBNull.Value;
                    entityId.Value = row.EntityId;
                    payload.Value = row.Payload;
                    groupKey.Value = (object?)row.GroupKey ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Records build progress so it can be reported while a snapshot is being materialised.
    /// </summary>
    /// <param name="generation">Target snapshot.</param>
    /// <param name="phase">Phase name.</param>
    /// <param name="done">Items completed in this phase.</param>
    /// <param name="total">Items expected in this phase.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the progress is recorded.</returns>
    public Task SetProgressAsync(
        long generation,
        string phase,
        long done,
        long total,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                UPDATE snapshots SET phase = $phase, phase_done = $done, phase_total = $total
                 WHERE generation = $generation;
                """,
                ("$phase", phase),
                ("$done", done),
                ("$total", total),
                ("$generation", generation)),
            cancellationToken);

    /// <summary>
    /// Marks a snapshot complete and streamable.
    /// </summary>
    /// <param name="generation">Target snapshot.</param>
    /// <param name="rowCount">Total rows written.</param>
    /// <param name="byteCount">Total payload bytes.</param>
    /// <param name="expiresAt">When the snapshot may be reclaimed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the snapshot is marked complete.</returns>
    public Task CompleteAsync(
        long generation,
        long rowCount,
        long byteCount,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                UPDATE snapshots
                   SET state = 'complete', row_count = $rows, byte_count = $bytes,
                       completed_at = $now, expires_at = $expires,
                       phase = NULL, error_code = NULL, error_detail = NULL
                 WHERE generation = $generation AND state = 'building';
                """,
                ("$rows", rowCount),
                ("$bytes", byteCount),
                ("$now", Now()),
                ("$expires", expiresAt.ToUnixTimeMilliseconds()),
                ("$generation", generation)),
            cancellationToken);

    /// <summary>
    /// Marks a snapshot failed.
    /// </summary>
    /// <param name="generation">Target snapshot.</param>
    /// <param name="errorCode">Machine-readable reason.</param>
    /// <param name="errorDetail">Administrator-facing detail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the failure is recorded.</returns>
    public Task FailAsync(
        long generation,
        string errorCode,
        string? errorDetail,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                UPDATE snapshots
                   SET state = 'failed', error_code = $code, error_detail = $detail
                 WHERE generation = $generation AND state = 'building';
                """,
                ("$code", errorCode),
                ("$detail", (object?)errorDetail ?? DBNull.Value),
                ("$generation", generation)),
            cancellationToken);

    /// <summary>
    /// Invalidates every snapshot left mid-build, and discards their partial rows.
    /// </summary>
    /// <remarks>
    /// Called at startup. A <c>building</c> row that survives a restart has no worker behind it and
    /// will never complete; leaving it would let a session wait on a snapshot that is never coming.
    /// Marking rather than deleting keeps the failure visible in diagnostics.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many snapshots were invalidated.</returns>
    public Task<int> InvalidateInterruptedBuildsAsync(CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) =>
            {
                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    DELETE FROM snapshot_rows
                     WHERE generation IN (SELECT generation FROM snapshots WHERE state = 'building');
                    """);

                return SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE snapshots
                       SET state = 'invalidated', error_code = 'serverRestart',
                           error_detail = 'The server restarted while this snapshot was being built.'
                     WHERE state = 'building';
                    """);
            },
            cancellationToken);

    /// <summary>
    /// Deletes a snapshot and its rows.
    /// </summary>
    /// <param name="generation">Target snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the snapshot is gone.</returns>
    public Task DeleteAsync(long generation, CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) =>
            {
                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    "DELETE FROM snapshot_rows WHERE generation = $generation;",
                    ("$generation", generation));

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    "DELETE FROM snapshots WHERE generation = $generation;",
                    ("$generation", generation));
            },
            cancellationToken);

    /// <summary>
    /// Reads one snapshot's metadata.
    /// </summary>
    /// <param name="generation">Target snapshot.</param>
    /// <returns>The metadata, or null when the generation is unknown.</returns>
    public SnapshotInfo? Get(long generation)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE generation = $generation;";
        command.Parameters.AddWithValue("$generation", generation);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadInfo(reader) : null;
    }

    /// <summary>
    /// Finds a completed snapshot for a user that is recent enough to reuse.
    /// </summary>
    /// <remarks>
    /// This is what makes a second device cheap: it joins the snapshot the first device already
    /// paid for instead of rebuilding an identical one.
    /// </remarks>
    /// <param name="userId">Owning user.</param>
    /// <param name="wireSchema">Wire schema the client negotiated.</param>
    /// <param name="completedAfter">Earliest completion time still considered fresh.</param>
    /// <returns>A reusable snapshot, or null.</returns>
    public SnapshotInfo? FindReusable(Guid userId, int wireSchema, DateTimeOffset completedAfter)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns +
            """
             WHERE user_id = $user AND state = 'complete' AND wire_schema = $schema
               AND completed_at >= $after
             ORDER BY completed_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$schema", wireSchema);
        command.Parameters.AddWithValue("$after", completedAfter.ToUnixTimeMilliseconds());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadInfo(reader) : null;
    }

    /// <summary>
    /// Finds the most recent snapshot for a user, in any state.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <returns>The latest snapshot, or null.</returns>
    public SnapshotInfo? FindLatest(Guid userId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE user_id = $user ORDER BY generation DESC LIMIT 1;";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadInfo(reader) : null;
    }

    /// <summary>
    /// Counts snapshot builds started for a user since a point in time.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="since">Window start.</param>
    /// <returns>How many builds began in the window.</returns>
    public long BuildsSince(Guid userId, DateTimeOffset since)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM snapshots WHERE user_id = $user AND created_at >= $since;";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the highest ordinal in a snapshot.
    /// </summary>
    /// <remarks>
    /// This is the delivery upper bound, and it is <b>not</b> the row count. Ordinal ranges are
    /// reserved per phase during a build, so unused reservations leave gaps; comparing a delivered
    /// ordinal against the row count would report catch-up early and truncate the client's library.
    /// </remarks>
    /// <param name="generation">Target snapshot.</param>
    /// <returns>The highest ordinal, or zero when the snapshot has no rows.</returns>
    public long MaxOrdinal(long generation)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(ordinal), 0) FROM snapshot_rows WHERE generation = $generation;";
        command.Parameters.AddWithValue("$generation", generation);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Publishes how far a partially built snapshot may be delivered.
    /// </summary>
    /// <remarks>
    /// Only ever moves forward. The builder writes ordinals in ascending order and calls this after
    /// each batch is committed, so a reader that respects the watermark can never observe a row that
    /// is still being written or a range that is about to be filled in behind it.
    /// </remarks>
    /// <param name="generation">Target snapshot.</param>
    /// <param name="throughOrdinal">Highest ordinal now safe to deliver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the watermark is recorded.</returns>
    public Task SetStreamableThroughAsync(
        long generation,
        long throughOrdinal,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                UPDATE snapshots
                   SET streamable_through = MAX(streamable_through, $through)
                 WHERE generation = $generation;
                """,
                ("$through", throughOrdinal),
                ("$generation", generation)),
            cancellationToken);

    /// <summary>
    /// Totals the payload bytes a snapshot holds.
    /// </summary>
    /// <remarks>
    /// One aggregate rather than reading every row back. The build used to re-read the entire
    /// finished snapshot to hash it, and took the byte total from that pass; with the hashing gone
    /// there is no reason to move 16 MB through memory to add up its lengths.
    /// </remarks>
    /// <param name="generation">Target snapshot.</param>
    /// <returns>Total payload bytes.</returns>
    public long PayloadBytes(long generation)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(SUM(LENGTH(payload)), 0) FROM snapshot_rows WHERE generation = $generation;";
        command.Parameters.AddWithValue("$generation", generation);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads rows for delivery.
    /// </summary>
    /// <remarks>
    /// Bounded by both count and payload bytes. The byte bound is checked <i>after</i> adding each
    /// row so that a single oversized record is still emitted whole rather than becoming a row that
    /// can never be delivered.
    /// </remarks>
    /// <param name="generation">Target snapshot.</param>
    /// <param name="afterOrdinal">Exclusive lower bound.</param>
    /// <param name="maxRecords">Maximum rows to return.</param>
    /// <param name="maxPayloadBytes">Approximate payload budget.</param>
    /// <param name="throughOrdinal">
    /// Inclusive upper bound — the delivery watermark. Defaults to unbounded, which is correct for
    /// callers that own the build (retention, verification) and wrong for delivery, which must never
    /// hand out a row above the watermark.
    /// </param>
    /// <returns>Rows in ascending ordinal order.</returns>
    public IReadOnlyList<SnapshotRow> ReadAfter(
        long generation,
        long afterOrdinal,
        int maxRecords,
        long maxPayloadBytes,
        long throughOrdinal = long.MaxValue)
    {
        var rows = new List<SnapshotRow>(Math.Min(maxRecords, 1024));
        long bytes = 0;

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ordinal, kind, entity_type, entity_id, payload, group_key
              FROM snapshot_rows
             WHERE generation = $generation AND ordinal > $after AND ordinal <= $through
             ORDER BY ordinal
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$after", afterOrdinal);
        command.Parameters.AddWithValue("$through", throughOrdinal);
        command.Parameters.AddWithValue("$limit", maxRecords);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var payload = (byte[])reader["payload"];

            rows.Add(new SnapshotRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                payload,
                reader.IsDBNull(5) ? null : reader.GetString(5)));

            bytes += payload.Length;
            if (bytes >= maxPayloadBytes)
            {
                break;
            }
        }

        return rows;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static DateTimeOffset? ToOffset(object value) =>
        value is DBNull ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(value, CultureInfo.InvariantCulture));

    private static SnapshotInfo ReadInfo(SqliteDataReader reader) => new SnapshotInfo
    {
        Generation = reader.GetInt64(0),
        UserId = Guid.ParseExact(reader.GetString(1), "N"),
        State = reader.GetString(2),
        BaselineSequence = reader.GetInt64(3),
        WireSchema = reader.GetInt32(4),
        RowCount = reader.GetInt64(5),
        ByteCount = reader.GetInt64(6),
        Phase = reader.IsDBNull(7) ? null : reader.GetString(7),
        PhaseDone = reader.GetInt64(8),
        PhaseTotal = reader.GetInt64(9),
        ErrorCode = reader.IsDBNull(10) ? null : reader.GetString(10),
        ErrorDetail = reader.IsDBNull(11) ? null : reader.GetString(11),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
        CompletedAt = ToOffset(reader.GetValue(13)),
        ExpiresAt = ToOffset(reader.GetValue(14)),
        StreamableThrough = reader.GetInt64(15),
        RepairSince = ToOffset(reader.GetValue(16))
    };
}
