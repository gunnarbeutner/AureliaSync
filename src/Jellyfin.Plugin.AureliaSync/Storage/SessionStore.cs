using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Sessions, per-client subscriptions, and acknowledgement receipts.
/// </summary>
public sealed class SessionStore
{
    private const string SelectSession =
        """
        SELECT id, user_id, client_id, mode, protocol_version, wire_schema, generation,
               baseline_sequence, upper_bound, highest_issued_ordinal, acked_ordinal, state,
               created_at, last_seen_at, expires_at,
               segments_delivered, records_delivered, bytes_delivered, last_error_correlation, reason
          FROM sessions
        """;

    private readonly SyncDatabase _database;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionStore"/> class.
    /// </summary>
    /// <param name="database">The plugin database.</param>
    public SessionStore(SyncDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Creates an unguessable session identifier.
    /// </summary>
    /// <remarks>
    /// 32 random bytes in base64url. It appears in a URL path, so it must contain no slash or
    /// padding; and since it is effectively the capability for an open session, it must not be
    /// enumerable.
    /// </remarks>
    /// <returns>A new identifier.</returns>
    public static string NewSessionId() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Opens a session, superseding any other live session for the same client.
    /// </summary>
    /// <param name="session">The session to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the session exists.</returns>
    public Task CreateAsync(SessionInfo session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _database.WriteAsync(
            (connection, transaction) =>
            {
                // One active session per client: a second device is a different client, but the
                // same client reconnecting should not leave its previous session holding state.
                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE sessions SET state = 'closed'
                     WHERE user_id = $user AND client_id = $client
                       AND state IN ('preparing', 'streaming', 'snapshotComplete');
                    """,
                    ("$user", session.UserId.ToString("N")),
                    ("$client", session.ClientId));

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    INSERT INTO sessions (id, user_id, client_id, mode, protocol_version, wire_schema,
                                          generation, baseline_sequence, upper_bound,
                                          highest_issued_ordinal, acked_ordinal, state,
                                          created_at, last_seen_at, expires_at, reason)
                    VALUES ($id, $user, $client, $mode, $protocol, $schema, $generation, $baseline,
                            $upper, $issued, $acked, $state, $now, $now, $expires, $reason);
                    """,
                    ("$id", session.Id),
                    ("$user", session.UserId.ToString("N")),
                    ("$client", session.ClientId),
                    ("$mode", session.Mode),
                    ("$protocol", session.ProtocolVersion),
                    ("$schema", session.WireSchema),
                    ("$generation", (object?)session.Generation ?? DBNull.Value),
                    ("$baseline", session.BaselineSequence),
                    ("$upper", session.UpperBound),
                    ("$issued", session.HighestIssuedOrdinal),
                    ("$acked", session.AckedOrdinal),
                    ("$state", session.State),
                    ("$now", Now()),
                    ("$expires", session.ExpiresAt.ToUnixTimeMilliseconds()),
                    ("$reason", (object?)session.Reason ?? DBNull.Value));
            },
            cancellationToken);
    }

    /// <summary>
    /// Reads a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>The session, or null when unknown.</returns>
    public SessionInfo? Get(string sessionId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSession + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sessionId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    /// <summary>
    /// Records that a session delivered up to an ordinal.
    /// </summary>
    /// <remarks>
    /// Called <b>before</b> the closing line of a segment is written, so that an acknowledgement
    /// for a cursor the client actually received always validates — even if the connection dies
    /// mid-flush and the client retries from what it has.
    /// </remarks>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="issuedOrdinal">Highest ordinal placed on the wire.</param>
    /// <param name="expiresAt">Refreshed idle expiry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once recorded.</returns>
    public Task RecordIssuedAsync(
        string sessionId,
        long issuedOrdinal,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default) =>
        RecordIssuedAsync(sessionId, issuedOrdinal, expiresAt, 0, 0, cancellationToken);

    /// <summary>
    /// Records delivery progress along with what the segment contained.
    /// </summary>
    /// <remarks>
    /// The counters ride on the write that already happens once per segment, so observability here
    /// costs nothing extra. They are what makes a sync that failed on the client side legible from
    /// the server: without them a session records the state it reached but not what it sent.
    /// </remarks>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="issuedOrdinal">Highest ordinal placed on the wire.</param>
    /// <param name="expiresAt">Refreshed idle expiry.</param>
    /// <param name="records">Records in the segment just written.</param>
    /// <param name="bytes">Payload bytes in the segment just written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once recorded.</returns>
    public Task RecordIssuedAsync(
        string sessionId,
        long issuedOrdinal,
        DateTimeOffset expiresAt,
        long records,
        long bytes,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                UPDATE sessions
                   SET highest_issued_ordinal = MAX(highest_issued_ordinal, $issued),
                       state = CASE WHEN state = 'preparing' THEN 'streaming' ELSE state END,
                       segments_delivered = segments_delivered + 1,
                       records_delivered = records_delivered + $records,
                       bytes_delivered = bytes_delivered + $bytes,
                       last_seen_at = $now, expires_at = $expires
                 WHERE id = $id;
                """,
                ("$issued", issuedOrdinal),
                ("$records", records),
                ("$bytes", bytes),
                ("$now", Now()),
                ("$expires", expiresAt.ToUnixTimeMilliseconds()),
                ("$id", sessionId)),
            cancellationToken);

    /// <summary>
    /// Records that a session failed, so the correlation identifier can be found later.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="errorCode">Machine-readable code.</param>
    /// <param name="correlationId">Correlation identifier tying this to the server log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once recorded.</returns>
    public Task RecordErrorAsync(
        string sessionId,
        string errorCode,
        string correlationId,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                UPDATE sessions SET error_code = $code, last_error_correlation = $correlation
                 WHERE id = $id;
                """,
                ("$code", errorCode),
                ("$correlation", correlationId),
                ("$id", sessionId)),
            cancellationToken);

    /// <summary>
    /// Reads recent sessions for administrator diagnostics.
    /// </summary>
    /// <param name="limit">How many to return, newest first.</param>
    /// <returns>Recent sessions.</returns>
    public IReadOnlyList<SessionInfo> RecentSessions(int limit)
    {
        var sessions = new List<SessionInfo>();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSession + " ORDER BY created_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    /// <summary>
    /// Reads every subscription for administrator diagnostics.
    /// </summary>
    /// <returns>All subscriptions with their positions.</returns>
    public IReadOnlyList<(string ClientId, SubscriptionInfo Subscription)> AllSubscriptions()
    {
        var rows = new List<(string, SubscriptionInfo)>();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT client_id, ack_sequence, snapshot_generation, snapshot_acked, state, reason
              FROM subscriptions ORDER BY last_seen_at DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), new SubscriptionInfo
            {
                AckSequence = reader.GetInt64(1),
                SnapshotGeneration = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                SnapshotAcked = reader.GetInt32(3) == 1,
                State = reader.GetString(4),
                Reason = reader.IsDBNull(5) ? null : reader.GetString(5)
            }));
        }

        return rows;
    }

    /// <summary>
    /// Attaches a snapshot to a session that was waiting for one to finish building.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="generation">The snapshot now being delivered.</param>
    /// <param name="upperBound">Its highest ordinal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once attached.</returns>
    public Task AttachSnapshotAsync(
        string sessionId,
        long generation,
        long upperBound,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                UPDATE sessions SET generation = $generation, upper_bound = $upper, last_seen_at = $now
                 WHERE id = $id;
                """,
                ("$generation", generation),
                ("$upper", upperBound),
                ("$now", Now()),
                ("$id", sessionId)),
            cancellationToken);

    /// <summary>
    /// Closes a session, preserving its client's checkpoint.
    /// </summary>
    /// <remarks>
    /// Idempotent and tolerant of unknown identifiers: the client fires this from a deferred task,
    /// so it races the final acknowledgement and is also sent for sessions that already failed.
    /// </remarks>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="userId">The caller, so one user cannot close another's session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once closed.</returns>
    public Task CloseAsync(string sessionId, Guid userId, CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                "UPDATE sessions SET state = 'closed', last_seen_at = $now WHERE id = $id AND user_id = $user;",
                ("$now", Now()),
                ("$id", sessionId),
                ("$user", userId.ToString("N"))),
            cancellationToken);

    /// <summary>
    /// Applies a cumulative acknowledgement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole thing runs in one immediate transaction, and the validation order matters:
    /// </para>
    /// <list type="number">
    /// <item>
    /// A receipt for this <c>(user, client, clientCommitId)</c> is looked up <b>first, before the
    /// session is even considered</b>. The client commits a segment locally before acknowledging
    /// it, and after a crash replays that exact acknowledgement to that exact session. If the
    /// session has since expired, refusing would strand a client that did everything right — so a
    /// replay returns the stored result no matter what became of the session.
    /// </item>
    /// <item>Then the session must exist, belong to the caller, and be live.</item>
    /// <item>Then the cursor must belong to this generation and have actually been issued.</item>
    /// <item>Only then does the checkpoint move, monotonically.</item>
    /// </list>
    /// </remarks>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="userId">Authenticated caller.</param>
    /// <param name="clientId">Calling client installation.</param>
    /// <param name="clientCommitId">The client's idempotency key for this acknowledgement.</param>
    /// <param name="generation">Generation encoded in the acknowledged cursor.</param>
    /// <param name="ordinal">Ordinal encoded in the acknowledged cursor.</param>
    /// <param name="snapshotRowCount">Total rows in the snapshot, used to detect completion.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, and the resulting checkpoint.</returns>
    public Task<AckOutcome> AcknowledgeAsync(
        string sessionId,
        Guid userId,
        string clientId,
        string clientCommitId,
        long generation,
        long ordinal,
        long snapshotRowCount,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) =>
            {
                var user = userId.ToString("N");

                // 1. Replay, independent of session lifetime.
                var stored = ReadReceipt(connection, transaction, user, clientId, clientCommitId);
                if (stored is { } previous)
                {
                    return new AckOutcome(
                        AckResult.AlreadyApplied, previous, previous >= snapshotRowCount && snapshotRowCount > 0);
                }

                // 2. The session must be the caller's and usable.
                var session = ReadSessionFor(connection, transaction, sessionId, user);
                if (session is null || !session.IsLive)
                {
                    return new AckOutcome(AckResult.SessionUnusable, 0, false);
                }

                // 3. The cursor must belong to this delivery.
                // A change session's cursors address journal sequences and carry no generation, so
                // the snapshot-generation check does not apply to them.
                if (!string.Equals(session.Mode, "changes", StringComparison.Ordinal)
                    && session.Generation is { } sessionGeneration
                    && sessionGeneration != generation)
                {
                    return new AckOutcome(AckResult.WrongGeneration, session.AckedOrdinal, false);
                }

                if (ordinal > session.HighestIssuedOrdinal)
                {
                    return new AckOutcome(AckResult.BeyondIssued, session.AckedOrdinal, false);
                }

                // 4. Advance, monotonically. A lower cursor is accepted and changes nothing.
                var resulting = Math.Max(session.AckedOrdinal, ordinal);
                var complete = snapshotRowCount > 0 && resulting >= session.UpperBound;

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE sessions
                       SET acked_ordinal = $acked, last_seen_at = $now,
                           state = CASE WHEN $complete = 1 THEN 'snapshotComplete' ELSE state END
                     WHERE id = $id;
                    """,
                    ("$acked", resulting),
                    ("$now", Now()),
                    ("$complete", complete ? 1 : 0),
                    ("$id", sessionId));

                UpsertSubscription(connection, transaction, user, clientId, session, resulting, complete);
                WriteReceipt(connection, transaction, user, clientId, clientCommitId, sessionId, resulting);

                return new AckOutcome(
                    ordinal <= session.AckedOrdinal ? AckResult.NoOp : AckResult.Advanced, resulting, complete);
            },
            cancellationToken);

    /// <summary>
    /// Reads how many of a user's clients hold a completed checkpoint.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The number of active, snapshot-complete subscriptions.</returns>
    public long ActiveSubscriptionCount(Guid userId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM subscriptions WHERE user_id = $user AND state = 'active' AND snapshot_acked = 1;";
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a client's durable position, or null when it has none.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="clientId">The client installation.</param>
    /// <returns>Its subscription, or null.</returns>
    public SubscriptionInfo? GetSubscription(Guid userId, string clientId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ack_sequence, snapshot_generation, snapshot_acked, state, reason, last_ack_at
              FROM subscriptions WHERE user_id = $user AND client_id = $client;
            """;
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$client", clientId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SubscriptionInfo
        {
            AckSequence = reader.GetInt64(0),
            SnapshotGeneration = reader.IsDBNull(1) ? null : reader.GetInt64(1),
            SnapshotAcked = reader.GetInt32(2) == 1,
            State = reader.GetString(3),
            Reason = reader.IsDBNull(4) ? null : reader.GetString(4),
            LastAckAt = reader.IsDBNull(5)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))
        };
    }

    /// <summary>
    /// Advances a client's journal position after it acknowledges changes.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="clientId">The client installation.</param>
    /// <param name="sequence">The acknowledged journal sequence.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once recorded.</returns>
    public Task AdvanceJournalPositionAsync(
        Guid userId, string clientId, long sequence, CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                UPDATE subscriptions
                   SET ack_sequence = MAX(ack_sequence, $sequence), last_ack_at = $now, last_seen_at = $now
                 WHERE user_id = $user AND client_id = $client;
                """,
                ("$sequence", sequence),
                ("$now", Now()),
                ("$user", userId.ToString("N")),
                ("$client", clientId)),
            cancellationToken);

    /// <summary>
    /// Returns the users who hold a completed checkpoint.
    /// </summary>
    /// <remarks>
    /// Only these need journal records. A user without a checkpoint will take a snapshot the first
    /// time they connect, so journalling changes for them writes rows nobody will ever read.
    /// </remarks>
    /// <returns>Identifiers of users with an active, snapshot-complete subscription.</returns>
    public IReadOnlySet<Guid> ActiveSubscriberIds()
    {
        var ids = new HashSet<Guid>();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT user_id FROM subscriptions
             WHERE state = 'active' AND snapshot_acked = 1;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Guid.TryParseExact(reader.GetString(0), "N", out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Counts sessions a client opened since a point in time, for rate limiting.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="clientId">The client installation.</param>
    /// <param name="since">Window start.</param>
    /// <returns>How many sessions were created in the window.</returns>
    public long SessionsCreatedSince(Guid userId, string clientId, DateTimeOffset since)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM sessions
             WHERE user_id = $user AND client_id = $client AND created_at >= $since;
            """;
        command.Parameters.AddWithValue("$user", userId.ToString("N"));
        command.Parameters.AddWithValue("$client", clientId);
        command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Marks a client as needing a fresh snapshot, discarding its checkpoint.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="clientId">The client installation.</param>
    /// <param name="reason">Why, for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once recorded.</returns>
    public Task ResetSubscriptionAsync(
        Guid userId,
        string clientId,
        string reason,
        CancellationToken cancellationToken = default) =>
        _database.WriteAsync(
            (connection, transaction) =>
            {
                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE subscriptions
                       SET state = 'snapshotRequired', reason = $reason, snapshot_acked = 0,
                           ack_sequence = 0, snapshot_generation = NULL
                     WHERE user_id = $user AND client_id = $client;
                    """,
                    ("$reason", reason),
                    ("$user", userId.ToString("N")),
                    ("$client", clientId));

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE sessions SET state = 'closed'
                     WHERE user_id = $user AND client_id = $client
                       AND state IN ('preparing', 'streaming', 'snapshotComplete');
                    """,
                    ("$user", userId.ToString("N")),
                    ("$client", clientId));
            },
            cancellationToken);

    private static long? ReadReceipt(
        SqliteConnection connection, SqliteTransaction transaction, string user, string client, string commitId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT resulting_ordinal FROM ack_requests
             WHERE user_id = $user AND client_id = $client AND client_commit_id = $commit;
            """;
        command.Parameters.AddWithValue("$user", user);
        command.Parameters.AddWithValue("$client", client);
        command.Parameters.AddWithValue("$commit", commitId);

        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void WriteReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string user,
        string client,
        string commitId,
        string sessionId,
        long resulting) =>
        SyncDatabase.ExecuteWithParameters(
            connection,
            transaction,
            """
            INSERT INTO ack_requests (user_id, client_id, client_commit_id, session_id,
                                      resulting_ordinal, resulting_sequence, created_at)
            VALUES ($user, $client, $commit, $session, $ordinal, 0, $now)
            ON CONFLICT(user_id, client_id, client_commit_id) DO NOTHING;
            """,
            ("$user", user),
            ("$client", client),
            ("$commit", commitId),
            ("$session", sessionId),
            ("$ordinal", resulting),
            ("$now", Now()));

    private static void UpsertSubscription(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string user,
        string client,
        SessionInfo session,
        long resulting,
        bool complete) =>
        SyncDatabase.ExecuteWithParameters(
            connection,
            transaction,
            """
            INSERT INTO subscriptions (user_id, client_id, ack_sequence, snapshot_generation,
                                       snapshot_acked, state, protocol_version, wire_schema,
                                       created_at, last_seen_at, last_ack_at, expires_at)
            VALUES ($user, $client, $sequence, $generation, $acked, 'active', $protocol, $schema,
                    $now, $now, $now, $expires)
            ON CONFLICT(user_id, client_id) DO UPDATE SET
                ack_sequence        = MAX(ack_sequence, excluded.ack_sequence),
                snapshot_generation = excluded.snapshot_generation,
                snapshot_acked      = MAX(snapshot_acked, excluded.snapshot_acked),
                state               = 'active',
                reason              = NULL,
                protocol_version    = excluded.protocol_version,
                wire_schema         = excluded.wire_schema,
                last_seen_at        = excluded.last_seen_at,
                last_ack_at         = excluded.last_ack_at,
                expires_at          = excluded.expires_at;
            """,
            ("$user", user),
            ("$client", client),
            // The snapshot's baseline is the journal position a later change session resumes from;
            // it only becomes the client's position once the whole snapshot is acknowledged.
            ("$sequence", complete ? session.BaselineSequence : 0L),
            ("$generation", (object?)session.Generation ?? DBNull.Value),
            ("$acked", complete ? 1 : 0),
            ("$protocol", session.ProtocolVersion),
            ("$schema", session.WireSchema),
            ("$now", Now()),
            ("$expires", DateTimeOffset.UtcNow.AddDays(90).ToUnixTimeMilliseconds()),
            ("$resulting", resulting));

    private static SessionInfo? ReadSessionFor(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId, string user)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectSession + " WHERE id = $id AND user_id = $user;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$user", user);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static SessionInfo ReadSession(SqliteDataReader reader) => new SessionInfo
    {
        Id = reader.GetString(0),
        UserId = Guid.ParseExact(reader.GetString(1), "N"),
        ClientId = reader.GetString(2),
        Mode = reader.GetString(3),
        ProtocolVersion = reader.GetInt32(4),
        WireSchema = reader.GetInt32(5),
        Generation = reader.IsDBNull(6) ? null : reader.GetInt64(6),
        BaselineSequence = reader.GetInt64(7),
        UpperBound = reader.GetInt64(8),
        HighestIssuedOrdinal = reader.GetInt64(9),
        AckedOrdinal = reader.GetInt64(10),
        State = reader.GetString(11),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(12)),
        LastSeenAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(13)),
        ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
        SegmentsDelivered = reader.GetInt64(15),
        RecordsDelivered = reader.GetInt64(16),
        BytesDelivered = reader.GetInt64(17),
        LastErrorCorrelation = reader.IsDBNull(18) ? null : reader.GetString(18),
        Reason = reader.IsDBNull(19) ? null : reader.GetString(19)
    };
}
