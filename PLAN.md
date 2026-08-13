# AureliaSync Jellyfin Plugin Plan

## Purpose

AureliaSync is a Jellyfin server plugin that gives Aurelia a reliable, efficient
library synchronization protocol. Its central abstraction is a durable change
journal with per-client checkpoints. Clients consume bounded streaming sessions
and cumulatively acknowledge only the changes they have committed to their local
SQLite database.

The plugin replaces Aurelia's repeated, offset-based crawls across Jellyfin's
artists, albums, tracks, playlists, genres, and playlist contents. The existing
Jellyfin API remains the fallback when the plugin is unavailable.

## Goals

- Resume an interrupted sync without repeating an entire stage or catalog crawl.
- Deliver a stable initial snapshot while library changes continue concurrently.
- Deliver subsequent metadata, relationship, deletion, and user-data changes in
  a deterministic order.
- Let each Aurelia installation acknowledge the highest contiguous cursor it has
  durably processed.
- Make all delivery idempotent: reconnecting or replaying a response must be safe.
- Preserve good client state when a request, session, or server operation fails.
- Support multiple users and multiple Aurelia devices independently.
- Bound server storage through explicit retention and checkpoint-expiry rules.
- Install and update through a GitHub-hosted Jellyfin plugin repository.

## Non-goals

- Replacing Jellyfin's media streaming or playback APIs.
- Keeping a database transaction open for the lifetime of a client sync.
- Exposing Jellyfin's internal database schema to Aurelia.
- Promising exactly-once network delivery. The protocol provides ordered,
  at-least-once delivery with idempotent application and cumulative acknowledgement.
- Treating a WebSocket notification alone as proof that all relevant state was
  captured. Periodic reconciliation remains necessary.

## Core model

### Durable journal

The plugin owns a SQLite database in its Jellyfin plugin data directory. Every
observable library mutation is represented by an append-only journal record with
a monotonically increasing 64-bit `sequence`.

Journal records contain enough materialized data to be delivered later. They must
not consist only of a Jellyfin item ID that is hydrated during delivery, because
the item may have changed again or been deleted by then.

Proposed record kinds:

- `item.upsert`: artist, album, track, playlist, or genre wire representation.
- `item.delete`: stable item ID and entity kind, when known.
- `playlist.replace`: complete ordered membership for one playlist.
- `userData.upsert`: favorite, play count, last-played date, playback position,
  scoped to the authenticated Jellyfin user.
- `relationship.replace`: relationship sets that cannot be represented safely as
  an item upsert, such as album-artist membership if needed.
- `control.reconcile`: optional diagnostic marker for reconciliation boundaries.

Each record also stores its visibility scope (`catalog` or a Jellyfin user ID),
creation time, schema version, and serialized payload checksum.

### Client identity and checkpoint

Every Aurelia installation has a random `clientId` stored in Keychain, separate
from Jellyfin's access token and Aurelia's current UserDefaults device ID. It should
survive an app reinstall just as the saved Jellyfin credentials do.

The plugin stores one subscription row per `(server instance, Jellyfin user,
clientId)` with:

- highest cumulatively acknowledged journal sequence;
- current snapshot generation, if any;
- last contact and last acknowledgement times;
- client and protocol versions for diagnostics;
- expiry state and the reason a new snapshot is required.

The user ID is derived from the authenticated Jellyfin request. A caller must not
be allowed to select another user's scope in a request body.

### Delivery session

A session is a resumable, bounded view over durable state; it is not the durable
checkpoint itself. Losing a session must not lose the acknowledged checkpoint.

A session records:

- opaque, unguessable session ID;
- owning user and client ID;
- session mode (`snapshot` or `changes`);
- protocol and wire-schema versions;
- snapshot generation and snapshot baseline sequence, when applicable;
- journal upper bound captured when the session was opened;
- highest cursor issued in this session;
- creation, last-access, and expiry times.

Sessions may expire after a short period (proposed default: 24 hours). A client
then opens another session from its durable checkpoint. Active client checkpoints
have a longer retention window (proposed default: 90 days).

## Snapshot-to-change handoff

An initial sync cannot safely be implemented as ordinary offset pagination over a
live Jellyfin query. Inserts and deletions can shift offsets, and changes arriving
during the crawl can be missed.

The plugin should use this sequence:

1. Capture the current journal head as `baselineSequence`.
2. Materialize a stable, user-visible snapshot into plugin-owned snapshot tables.
   Snapshot rows use a deterministic order and immutable snapshot-local cursors.
3. Continue recording all concurrent changes in the journal after the baseline.
4. Stream the materialized snapshot to the client.
5. After the client acknowledges snapshot completion, stream journal records from
   `baselineSequence + 1` onward.
6. The client promotes its staged local catalog atomically, then applies the
   post-baseline journal changes in order.

The plugin must not hold a Jellyfin database transaction open while a client reads
the snapshot. Snapshot materialization is a finite server task. Jellyfin events
received during materialization are journaled, and a reconciliation pass at the
snapshot boundary repairs event races or missed notifications.

If the plugin cannot guarantee that its event capture covered the snapshot build
window, it must invalidate that snapshot and retry rather than presenting an
apparently complete catalog.

## Streaming transport

Use authenticated HTTPS with bounded NDJSON response segments rather than one
permanent connection. This retains streaming parsing and low memory use while
working through common reverse proxies and allowing straightforward retries.

Example request:

```http
GET /AureliaSync/v1/sessions/{sessionId}/stream?after={cursor}&maxRecords=1000&maxBytes=8388608
Accept: application/x-ndjson
Accept-Encoding: gzip
```

Each line is a complete envelope:

```json
{"cursor":"opaque-cursor","sequence":48152,"kind":"item.upsert","entityType":"track","payload":{},"checksum":"sha256:..."}
```

The final line is a segment marker:

```json
{"kind":"segment.end","cursor":"opaque-cursor","caughtUp":false,"sessionUpperBound":49211}
```

Rules:

- Cursors are opaque to clients even if the first implementation internally maps
  them to a snapshot row and/or journal sequence.
- A response ends at `maxRecords`, `maxBytes`, a short server time budget, or the
  session upper bound.
- The client parses incrementally but commits a useful batch in one SQLite
  transaction before acknowledging it.
- Retrying `after` the last acknowledged cursor may replay unacknowledged records.
- Compression is enabled, and payload sizes and record counts are capped.
- Optional long polling can hold a caught-up changes request briefly, but a
  permanent stream is not required for the first version.

## Acknowledgement protocol

```http
POST /AureliaSync/v1/sessions/{sessionId}/ack
Content-Type: application/json

{
  "throughCursor": "opaque-cursor",
  "clientCommitId": "uuid",
  "recordCount": 437,
  "aggregateChecksum": "sha256:..."
}
```

Acknowledgement semantics:

- `throughCursor` is cumulative and means every visible record up to that cursor
  has been durably committed locally.
- The server advances the checkpoint atomically and monotonically.
- Repeating the same acknowledgement is successful and has no additional effect.
- A lower cursor is accepted as an idempotent no-op.
- A cursor not issued to this session, beyond the highest issued cursor, belonging
  to another client, or skipping a required phase is rejected.
- `clientCommitId` makes request retries diagnosable and idempotent.
- Checksums detect truncation or client/server framing mistakes; they are not a
  substitute for TLS or package signing.
- The client never acknowledges records merely received into memory.
- Snapshot completion is acknowledged only after the staged catalog has been
  atomically promoted. Post-baseline journal changes are acknowledged only after
  their own SQLite commit.

Acknowledging changes must not immediately delete them. Retention is based on the
minimum checkpoint among active subscriptions plus a safety margin.

## Proposed HTTP API

All `/AureliaSync/v1` endpoints require normal Jellyfin authentication unless an
endpoint is explicitly marked as administrative.

### Capability and health

`GET /AureliaSync/v1/status`

Returns plugin version, protocol range, wire-schema range, Jellyfin ABI, journal
head and floor, health, reconciliation state, and whether the current user needs a
snapshot. This is intentionally cheap and is Aurelia's feature probe.

### Open or resume a session

`POST /AureliaSync/v1/sessions`

Request fields:

- `clientId`
- Aurelia app version
- supported protocol and schema versions
- optional last locally committed checkpoint token
- requested content capabilities

Response fields:

- `sessionId`, negotiated versions, and mode;
- first cursor and current server head;
- snapshot progress if materialization is still running;
- expiry time and recommended response limits.

If the server recognizes a valid checkpoint at or above the journal floor, it
opens a change session. Otherwise it returns or creates a snapshot session and a
machine-readable reason such as `newClient`, `checkpointExpired`, `journalGap`, or
`schemaChanged`.

### Inspect a session

`GET /AureliaSync/v1/sessions/{sessionId}`

Returns materialization/delivery progress and actionable failure details. Aurelia
can use this while the initial snapshot is being prepared.

### Stream a bounded segment

`GET /AureliaSync/v1/sessions/{sessionId}/stream`

Streams snapshot rows or journal records after an opaque cursor.

### Acknowledge durable application

`POST /AureliaSync/v1/sessions/{sessionId}/ack`

Atomically advances the per-client checkpoint as described above.

### Close a session

`DELETE /AureliaSync/v1/sessions/{sessionId}`

Releases ephemeral session state but preserves the durable client checkpoint.
Closing is an optimization; expiry provides cleanup after crashes.

### Reset a subscription

`POST /AureliaSync/v1/subscription/reset`

Explicitly abandons the current checkpoint and requests a fresh snapshot. It does
not delete the client's existing local library; Aurelia keeps serving that state
until the replacement snapshot is complete.

## Wire schema

Define Aurelia-owned versioned DTOs instead of returning raw Jellyfin internal
entities or relying on Jellyfin's `BaseItemDto` serialization forever.

The first schema must cover all data currently persisted by Aurelia:

- artists and album-artist status;
- albums, year/date, artwork tags, genres, and artist relationships;
- tracks, disc/track ordering, duration, album and artist relationships, artwork,
  and playback identifiers;
- playlists and complete ordered playlist membership;
- genres and their relationships if stored explicitly;
- per-user favorites, play count, last-played date, and playback position;
- tombstones for every deletable entity and relationship.

Every envelope includes wire-schema version, entity ID, revision/sequence, and
payload checksum. Unknown optional fields are ignored. An unsupported required
schema causes session negotiation to fail cleanly rather than corrupting the
client cache.

## Change capture and reconciliation

The plugin uses Jellyfin library and user-data event interfaces as invalidation
signals. A hosted background service resolves affected entities and appends
materialized journal records to the plugin database.

Important cases:

- Upserts coalesce only before they become visible to a session; published
  sequences remain immutable.
- Deletions always produce tombstones.
- Playlist changes produce a complete ordered membership replacement initially;
  membership diffs can be an optimization later.
- Artist and genre changes that Jellyfin does not report reliably are repaired by
  reconciliation.
- User-data records are scoped to the affected user and never leak to another
  authenticated user.
- A scheduled reconciliation compares a compact inventory of Jellyfin IDs and
  revisions with plugin state, writes repairs to the journal, and records health
  metrics. It also runs around snapshot creation.

The plugin database and Jellyfin database cannot share an atomic transaction, so
the stated consistency model is eventual and repaired, not fictional exactly-once
capture.

## Persistence schema outline

- `journal(sequence PK, scope, kind, entity_type, entity_id, schema_version,
  payload, checksum, created_at)`
- `subscriptions(user_id, client_id, ack_sequence, snapshot_generation,
  checkpoint_token, last_seen_at, last_ack_at, expires_at, state, PK(...))`
- `sessions(id PK, user_id, client_id, mode, baseline_sequence,
  upper_bound_sequence, highest_issued_cursor, created_at, last_seen_at,
  expires_at, state, error_code, error_detail)`
- `snapshots(generation PK, user_id, baseline_sequence, state, row_count,
  checksum, created_at, completed_at, expires_at)`
- `snapshot_rows(generation, ordinal, kind, entity_type, entity_id, payload,
  checksum, PK(generation, ordinal))`
- `inventory(scope, entity_type, entity_id, observed_revision, payload_checksum,
  last_seen_reconciliation)`
- `ack_requests(user_id, client_id, client_commit_id, resulting_checkpoint,
  created_at, PK(...))` with bounded retention
- `meta(key PK, value)` for database and journal schema versions

SQLite uses WAL mode, foreign keys, short transactions, and explicit migrations.
Plugin upgrades back up or transactionally migrate the database. A failed migration
must leave the previous plugin/database usable or fail closed with diagnostics.

## Retention and backpressure

Initial proposed defaults, configurable by a Jellyfin administrator:

- delivery session expiry: 24 hours after last access;
- completed snapshot retention: 48 hours;
- inactive subscription expiry: 90 days;
- acknowledged journal safety margin: 24 hours;
- maximum journal size: configurable, with health warnings before forced expiry;
- per-response maximum: 1,000 records or 8 MiB uncompressed;
- per-client concurrent sessions: one active session plus a brief superseded-session
  grace period.

Cleanup removes journal records only below every active subscription's required
floor. If size limits force removal past an inactive client's checkpoint, that
subscription is marked `snapshotRequired`; the next session must not silently skip
the gap.

A slow client cannot block storage forever. Rate limits apply per authenticated
user/client, and snapshot generation is deduplicated where visibility and schema
allow it.

## Authentication and security

- Reuse Jellyfin authentication and authorization middleware.
- Derive user identity and library visibility from the authenticated principal.
- Validate `clientId`, session ownership, cursor ownership, and negotiated schema
  on every request.
- Use opaque random session IDs and signed/opaque checkpoint tokens.
- Never accept a server URL, repository URL, filesystem path, or arbitrary type
  name through sync endpoints.
- Cap request bodies, query limits, decompressed payloads, and concurrent work.
- Avoid logging tokens, complete payloads, or sensitive user activity.
- Return correlation IDs and structured safe errors; retain detailed server-side
  diagnostics for administrators.

## GitHub distribution and installation

The plugin is not bundled in the Aurelia app.

Repository layout should include:

- plugin C# solution and tests;
- GPL-compatible license and public source;
- build metadata for every supported Jellyfin ABI;
- generated `manifest.json` on a stable GitHub Pages or raw-content URL;
- versioned ZIP artifacts and checksums in GitHub Releases;
- CI that builds, tests, packages, checks manifest checksums, and publishes releases.

Aurelia's management flow:

1. Probe `/AureliaSync/v1/status`.
2. Fetch the current Jellyfin user's policy.
3. If the plugin is absent/incompatible and the user is an administrator, offer
   an explicit `Install Aurelia Sync` action.
4. Read all configured Jellyfin plugin repositories, append Aurelia's repository
   if absent, and POST the entire preserved list back. Jellyfin's repository API
   replaces rather than appends.
5. Ask Jellyfin to install the compatible AureliaSync package by stable assembly
   GUID, optionally pinning the repository URL and selected version.
6. Explain that activation needs a Jellyfin restart and request explicit consent
   before invoking the restart API.
7. Reconnect, probe status, negotiate the protocol, and begin sync.

For a non-administrator, show the repository URL, plugin name, and restart steps,
then keep probing. Never request or retain a separate administrator credential.

Automatic updates should require a one-time `Allow Aurelia to manage its sync
plugin` opt-in. Download/install may then be automatic when a compatible release
is available, but server restart remains explicit. Compatibility is chosen using
the Jellyfin server version/ABI and manifest `targetAbi`; one universal binary
must not be assumed.

## Aurelia client integration

Introduce a `LibrarySyncTransport` abstraction with two implementations:

- `AureliaSyncPluginTransport`: sessions, bounded NDJSON streams, and acknowledgements.
- `JellyfinLegacySyncTransport`: current staged full crawl and timestamp overlap
  logic as a fallback.

Client algorithm:

1. Load the local plugin checkpoint and durable Keychain `clientId`.
2. Probe and negotiate plugin capabilities.
3. Open/resume a session with the last committed checkpoint.
4. Read a bounded segment incrementally.
5. Validate framing, ordering, schema, and checksums.
6. Apply the segment to SQLite in one transaction. For a snapshot, write to the
   existing staging tables; for changes, use the existing idempotent delta path.
7. Persist the new local checkpoint in the same SQLite transaction as the data.
8. POST the cumulative acknowledgement.
9. If acknowledgement fails, retry it; do not reapply unless the server replays
   the segment, in which case sequence/revision keys make application idempotent.
10. Continue until caught up, then poll/long-poll and respond to existing Jellyfin
    WebSocket invalidation hints.

The local checkpoint must never move ahead of the local data transaction. The
server acknowledgement may lag safely. A failed sync never clears or replaces the
last promoted local catalog.

## Failure semantics and structured errors

Use machine-readable error codes with human-readable detail and correlation IDs:

- `protocolNotSupported`
- `schemaNotSupported`
- `sessionExpired`
- `sessionNotOwned`
- `checkpointExpired`
- `journalGap`
- `snapshotPreparing`
- `snapshotInvalidated`
- `cursorInvalid`
- `ackBeyondIssued`
- `ackPhaseMismatch`
- `reconciliationRequired`
- `serverBusy`
- `storagePressure`

`sessionExpired` is recoverable by opening a new session. `checkpointExpired`,
`journalGap`, and incompatible schema require a new snapshot. None of these errors
instructs Aurelia to discard its current promoted library before a replacement is
fully committed.

## Observability

Expose safe status and administrator diagnostics for:

- journal head, floor, row count, and disk usage;
- active/expired subscriptions and sessions;
- snapshot state, duration, rows, and failures;
- oldest active acknowledgement and resulting retention pressure;
- event backlog and last successful reconciliation;
- records and bytes streamed, replayed, and acknowledged;
- plugin protocol/schema/ABI versions;
- last structured error and correlation ID.

Do not expose another user's listening data or raw payloads in normal status calls.

## Implementation phases

### Phase 1: Skeleton and distribution

- Scaffold the Jellyfin plugin, configuration, service registration, controller,
  plugin-owned SQLite database, migrations, and `/status` endpoint.
- Establish GitHub Actions packaging, ABI matrix, release ZIPs, checksums, and
  repository manifest.
- Prove administrator-driven repository addition, install, update, restart, and
  protocol probing against supported Jellyfin versions.

### Phase 2: Stable initial snapshot

- Define wire schema v1.
- Materialize music-library snapshots with deterministic rows and checksums.
- Implement session creation, inspection, bounded NDJSON streaming, expiry, and
  cumulative snapshot acknowledgements.
- Integrate with Aurelia's existing staged catalog and atomic promotion.

### Phase 3: Durable change journal

- Capture item upserts, deletions, user data, and playlist membership.
- Implement journal change sessions and snapshot-baseline handoff.
- Implement durable subscriptions, cumulative acknowledgements, replay, retention,
  cleanup, and forced snapshot rules.
- Switch Aurelia to plugin transport when protocol negotiation succeeds.

### Phase 4: Reconciliation and resilience

- Add compact inventory reconciliation and event-race repair.
- Exercise crashes at every boundary: snapshot build, partial stream, local commit,
  acknowledgement, plugin restart, Jellyfin restart, and upgrade.
- Add storage pressure, rate limits, structured diagnostics, and recovery UI.

### Phase 5: Optimization

- Deduplicate compatible snapshots, coalesce unpublished journal records, tune
  compression and segment sizes, and optionally add long polling.
- Consider compact relationship diffs only after replacement records are proven.

## Acceptance criteria

- Interrupting an initial sync at any byte/record boundary resumes without a full
  recrawl and never exposes a partial catalog.
- A change made while a snapshot is materializing appears after snapshot promotion
  without being lost.
- Killing Aurelia after its SQLite commit but before acknowledgement causes a safe
  replay and no duplicate visible data.
- Repeating an acknowledgement is harmless; acknowledging an unissued or skipped
  cursor is rejected.
- Two devices for the same user advance independent checkpoints.
- Two Jellyfin users receive only their own visible catalog and user data.
- Deletes and playlist reorder/removal events survive disconnects and server restart.
- Journal cleanup never crosses an active checkpoint; an expired checkpoint yields
  an explicit snapshot requirement.
- A failed request, plugin update, or reconciliation never clears Aurelia's last
  good local library.
- Plugin install/update selects an ABI-compatible GitHub release and preserves all
  pre-existing Jellyfin plugin repositories.
- Legacy Jellyfin sync remains usable when AureliaSync is absent or temporarily
  unhealthy.

## Decisions to validate with a prototype

- Which Jellyfin event interfaces cover music metadata, deletions, playlist
  membership, and per-user data reliably on each supported ABI.
- Whether snapshot materialization should store full envelopes or a generation of
  immutable entity revisions referenced by snapshot rows.
- Whether catalog journal records can be shared across users and filtered safely,
  or should be materialized per visibility scope.
- Exact session/subscription retention defaults for realistically large libraries.
- Whether bounded long polling materially improves freshness over Aurelia's current
  WebSocket invalidation hints.

