using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AureliaSync.Storage.Migrations;

/// <summary>
/// Creates the initial schema.
/// </summary>
/// <remarks>
/// <para>
/// The <c>journal</c> and <c>inventory</c> tables are created empty here even though nothing writes
/// to them until phase 3. Creating an empty table costs nothing, and it means the phase-3 rollout
/// does not have to migrate a database that may hold live client checkpoints.
/// </para>
/// <para>
/// All timestamps are Unix milliseconds UTC. All entity and user identifiers are Jellyfin GUIDs in
/// their 32-character lowercase hex form ("N"), which is both what Jellyfin serialises and what the
/// Aurelia client already stores.
/// </para>
/// </remarks>
public sealed class M001Initial : IMigration
{
    /// <inheritdoc />
    public int Version => 1;

    /// <inheritdoc />
    public string Name => "initial";

    /// <inheritdoc />
    public void Apply(SqliteConnection connection, SqliteTransaction transaction)
    {
        SyncDatabase.Execute(
            connection,
            """
            -- Database-level key/value settings: schema versions, HMAC key for phase-3
            -- checkpoint tokens, and similar singletons.
            CREATE TABLE meta (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            ) WITHOUT ROWID;

            -- One row per materialised snapshot. Only state='complete' may be streamed; a build
            -- interrupted by a restart is marked 'invalidated' rather than presented as whole.
            CREATE TABLE snapshots (
              generation        INTEGER PRIMARY KEY AUTOINCREMENT,
              user_id           TEXT    NOT NULL,
              state             TEXT    NOT NULL,   -- building|complete|failed|invalidated
              baseline_sequence INTEGER NOT NULL DEFAULT 0,
              wire_schema       INTEGER NOT NULL,
              row_count         INTEGER NOT NULL DEFAULT 0,
              byte_count        INTEGER NOT NULL DEFAULT 0,
              checksum          TEXT,
              phase             TEXT,
              phase_done        INTEGER NOT NULL DEFAULT 0,
              phase_total       INTEGER NOT NULL DEFAULT 0,
              error_code        TEXT,
              error_detail      TEXT,
              created_at        INTEGER NOT NULL,
              completed_at      INTEGER,
              expires_at        INTEGER
            );

            CREATE INDEX ix_snapshots_user_state
              ON snapshots (user_id, state, completed_at DESC);

            -- The materialised rows. 'payload' holds the exact UTF-8 JSON bytes that go on the
            -- wire, so streaming is a copy rather than a deserialise/reserialise round trip.
            CREATE TABLE snapshot_rows (
              generation  INTEGER NOT NULL,
              ordinal     INTEGER NOT NULL,
              kind        TEXT    NOT NULL,
              entity_type TEXT,
              entity_id   TEXT    NOT NULL,
              payload     BLOB    NOT NULL,
              checksum    TEXT,
              PRIMARY KEY (generation, ordinal)
            ) WITHOUT ROWID;

            -- A delivery session: a resumable bounded view over durable state. Losing one must
            -- never lose the checkpoint, which lives in 'subscriptions' instead.
            CREATE TABLE sessions (
              id                     TEXT    PRIMARY KEY,
              user_id                TEXT    NOT NULL,
              client_id              TEXT    NOT NULL,
              mode                   TEXT    NOT NULL,   -- snapshot|changes
              protocol_version       INTEGER NOT NULL,
              wire_schema            INTEGER NOT NULL,
              generation             INTEGER,
              baseline_sequence      INTEGER NOT NULL DEFAULT 0,
              upper_bound            INTEGER NOT NULL DEFAULT 0,
              highest_issued_ordinal INTEGER NOT NULL DEFAULT 0,
              acked_ordinal          INTEGER NOT NULL DEFAULT 0,
              state                  TEXT    NOT NULL,   -- preparing|streaming|snapshotComplete|closed|expired|failed
              error_code             TEXT,
              error_detail           TEXT,
              created_at             INTEGER NOT NULL,
              last_seen_at           INTEGER NOT NULL,
              expires_at             INTEGER NOT NULL
            );

            CREATE INDEX ix_sessions_owner   ON sessions (user_id, client_id, state);
            CREATE INDEX ix_sessions_expires ON sessions (expires_at);

            -- The durable per-client checkpoint. This is the row that must survive session loss,
            -- plugin restart, and plugin upgrade.
            CREATE TABLE subscriptions (
              user_id             TEXT    NOT NULL,
              client_id           TEXT    NOT NULL,
              ack_sequence        INTEGER NOT NULL DEFAULT 0,
              snapshot_generation INTEGER,
              snapshot_acked      INTEGER NOT NULL DEFAULT 0,
              state               TEXT    NOT NULL,   -- active|snapshotRequired|expired
              reason              TEXT,
              client_version      TEXT,
              protocol_version    INTEGER,
              wire_schema         INTEGER,
              created_at          INTEGER NOT NULL,
              last_seen_at        INTEGER NOT NULL,
              last_ack_at         INTEGER,
              expires_at          INTEGER NOT NULL,
              PRIMARY KEY (user_id, client_id)
            ) WITHOUT ROWID;

            CREATE INDEX ix_subscriptions_expires ON subscriptions (expires_at);

            -- Records the outcome of each acknowledgement so that a retried request returns the
            -- stored result verbatim instead of being reapplied.
            CREATE TABLE ack_requests (
              user_id            TEXT    NOT NULL,
              client_id          TEXT    NOT NULL,
              client_commit_id   TEXT    NOT NULL,
              session_id         TEXT    NOT NULL,
              resulting_ordinal  INTEGER NOT NULL,
              resulting_sequence INTEGER NOT NULL DEFAULT 0,
              created_at         INTEGER NOT NULL,
              PRIMARY KEY (user_id, client_id, client_commit_id)
            ) WITHOUT ROWID;

            CREATE INDEX ix_ack_requests_age ON ack_requests (created_at);

            -- Phase 3. Created empty now so that rolling out the change journal does not require
            -- migrating a database holding live checkpoints.
            CREATE TABLE journal (
              sequence    INTEGER PRIMARY KEY AUTOINCREMENT,
              scope       TEXT    NOT NULL,   -- 'catalog' or a user id
              kind        TEXT    NOT NULL,
              entity_type TEXT,
              entity_id   TEXT    NOT NULL,
              wire_schema INTEGER NOT NULL,
              payload     BLOB    NOT NULL,
              checksum    TEXT,
              created_at  INTEGER NOT NULL
            );

            CREATE INDEX ix_journal_scope ON journal (scope, sequence);

            -- Phase 4 reconciliation state.
            CREATE TABLE inventory (
              scope                   TEXT NOT NULL,
              entity_type             TEXT NOT NULL,
              entity_id               TEXT NOT NULL,
              observed_revision       TEXT,
              payload_checksum        TEXT,
              last_seen_reconciliation INTEGER,
              PRIMARY KEY (scope, entity_type, entity_id)
            ) WITHOUT ROWID;
            """,
            transaction);
    }
}
