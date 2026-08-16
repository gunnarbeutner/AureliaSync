# AureliaSync protocol v1

Authoritative contract between the AureliaSync Jellyfin plugin (server) and the Aurelia client.

This document is the source of truth. Where it and either implementation disagree, this document
wins and the implementation is wrong.

- **Protocol version:** 1
- **Wire schema version:** 2
- **Plugin GUID:** `3fbf911d-ab0c-46dc-81d6-b3317bb8b176`
- **Repository manifest:** `https://gunnarbeutner.github.io/AureliaSync/manifest.json`

---

## 1. Model

The server materialises a **snapshot**: an immutable, ordered sequence of records describing one
user's visible music library. Each record has an **ordinal**; a **cursor** is an opaque encoding of
a position in that sequence.

A client opens a **session**, drains the snapshot as a series of bounded **segments**, and
acknowledges each segment after committing it locally. The server records a per-client
**checkpoint**, handed back as an opaque **checkpoint token**, which is the only thing the client
needs in order to resume.

Delivery is ordered and at-least-once. Replay is always safe: applying a record twice must produce
the same result as applying it once.

Protocol v1 implements both modes. A client that has fully acknowledged a snapshot and whose
position is still covered by the journal receives `mode: "changes"`; anything else receives
`mode: "snapshot"`.

---

## 2. Transport and authentication

All endpoints live under `/AureliaSync/v1` and require normal Jellyfin authentication via the
`X-Emby-Authorization` header:

```
X-Emby-Authorization: MediaBrowser Token="<token>", Client="Aurelia", Device="<model>", DeviceId="<id>", Version="<version>"
```

**The token must be user-scoped.** API-key authentication is rejected with `403 userScopeRequired`:
an API key is not bound to a user, so there is no library visibility scope and no user data that
could be served safely.

The user is derived exclusively from the authenticated principal. **No request may specify a user**,
and no endpoint accepts a server URL, filesystem path, or type name.

`GET` responses set `Cache-Control: no-store`. The client uses `URLSession.shared` with a default
`URLCache`, so an unmarked response can be served stale from cache.

---

## 3. JSON conventions

### 3.1 Keys are exact and irregular

The client decodes with `JSONDecoder` using `.useDefaultKeys` — **no key conversion strategy**.
JSON keys must therefore match the client's Swift property names byte for byte, including
inconsistent capitalisation:

```
id  name  sortName  artistName  artistId  artistIDs  albumId
productionYear  duration  imageTag  indexNumber  parentIndexNumber
biography  dateCreated  genreIDs
playlistEntryID  playlistID  position
isFavorite  lastPlayedAt  playCount  playbackPositionTicks
```

Note `artistId` and `albumId` with a lowercase `d`, but `artistIDs`, `genreIDs`,
`playlistEntryID` and `playlistID` with uppercase. This is not tidy; it is the contract.

> **Server implementers:** pin every DTO property with an explicit `[JsonPropertyName]`. Do not
> rely on `JsonNamingPolicy.CamelCase` to produce these — it will not, for the uppercase-ID names.
> Jellyfin also configures MVC with `PropertyNamingPolicy = null`, so plugin controllers serialise
> in PascalCase by default.

Null-valued fields are **omitted**, never emitted as `null`. Over 34,500 records the difference is
megabytes. See §7.6 for the one place where this distinction changes meaning.

### 3.2 Identifiers

Every entity identifier is a Jellyfin GUID in **32-character lowercase hexadecimal** form, with no
dashes — .NET's `Guid.ToString("N")`. This is what Jellyfin itself serialises and what the client
already stores.

### 3.3 Dates

ISO-8601 UTC with **either zero or exactly three** fractional-second digits, always with a `Z`
suffix:

```
2026-08-13T15:48:55.123Z
2026-08-13T15:48:55Z
```

> **This is the single most likely silent interop break.** The client parses with
> `ISO8601DateFormatter` and `.withFractionalSeconds`, which accepts exactly three fractional
> digits or none. .NET's default round-trip format (`"O"`) emits **seven** and will fail to parse.
> Serialise explicitly as `yyyy-MM-ddTHH:mm:ss.fffZ`.

Affects `expiresAt`, `dateCreated` and `lastPlayedAt`.

### 3.4 Durations

`duration` is **seconds, as a JSON number** (fractional allowed) — not Jellyfin ticks.

The only ticks-valued field in the protocol is `playbackPositionTicks`, which is raw Jellyfin
100-nanosecond ticks.

---

## 4. Errors

Every non-2xx response carries:

```json
{
  "error": {
    "code": "sessionExpired",
    "message": "Human-readable detail, safe to display.",
    "correlationId": "8d2f…",
    "retryable": true,
    "requiresSnapshot": false
  }
}
```

`message` never contains tokens, payload contents, or any other user's data. `correlationId` ties
the response to the server log.

### 4.1 Never return 404 except for an unknown endpoint

The client maps **any** 404 to "the Aurelia Sync plugin is not installed", before it parses the
body. A 404 for an expired session would tell the user to reinstall the plugin.

| Situation | Status |
|---|---|
| Endpoint does not exist (plugin absent or too old) | `404` |
| Session expired, closed, or unknown | `410` |
| Cursor not issued to this session, or beyond the highest issued | `409` |
| Cursor malformed, or checkpoint token fails its signature | `400` |
| Version negotiation found no overlap | `409` |
| API-key (non-user-scoped) authentication | `403` |
| Rate limited | `429` with `Retry-After` |

A checkpoint token that is well-formed and correctly signed but refers to a snapshot generation
that no longer exists is **not an error** — see §6.2.

### 4.2 Codes

`protocolNotSupported`, `schemaNotSupported`, `sessionExpired`, `sessionNotOwned`,
`checkpointExpired`, `journalGap`, `snapshotInvalidated`, `cursorInvalid`, `ackBeyondIssued`,
`ackPhaseMismatch`, `serverBusy`, `storagePressure`, `storageUnavailable`,
`starting`, `disabled`, `userScopeRequired`, `badRequest`.

No error ever instructs the client to discard its current library. Even with
`requiresSnapshot: true`, the client keeps serving the last promoted catalog until a replacement is
fully committed.

---

## 5. Endpoints

### 5.1 `GET /AureliaSync/v1/status`

The capability probe. Called before every sync attempt and on a periodic poll, so it is cheap: it
runs **no Jellyfin library queries**, only small reads of the plugin's own database.

```json
{
  "plugin": "AureliaSync",
  "pluginVersion": "0.2.0.0",
  "protocolVersions":   { "min": 1, "max": 1 },
  "wireSchemaVersions": { "min": 2, "max": 2 },
  "serverVersion": "10.11.11",
  "targetAbi": "10.11.0.0",
  "health": "ok",
  "healthDetail": null,
  "enabled": true,
  "databaseSchemaVersion": 1,
  "journal":  { "head": 0, "floor": 0, "records": 0 },
  "snapshot": { "state": "complete", "generation": 17, "rowCount": 34512, "phase": null },
  "user":     { "id": "6f3a…", "hasCheckpoint": true, "needsSnapshot": false },
  "limits":   { "maxRecordsPerSegment": 1000, "maxBytesPerSegment": 8388608, "segmentTimeBudgetMs": 10000 },
  "serverTime": "2026-08-13T15:48:55.123Z",
  "correlationId": "8d2f…"
}
```

The client proceeds only when `health` is exactly `"ok"` and `enabled` is `true`, and when the
advertised version ranges overlap its own. `health` ∈ `ok` | `starting` | `degraded` |
`unavailable`. `snapshot.state` ∈ `none` | `building` | `complete` | `failed` | `invalidated`.

The `user` block describes the **calling user only**.

> `snapshot.generation` is a **number** here. In §5.2 `snapshotGeneration` is a **string**. Both
> client decoders are strict, so both shapes are required as written.

### 5.2 `POST /AureliaSync/v1/sessions`

Opens or resumes a session. **Returns immediately**, even when a snapshot must still be built —
see §6.1.

Request (note the flat version fields, not nested ranges):

```json
{
  "clientId": "b3f1…",
  "clientVersion": "1.1",
  "protocolMin": 1, "protocolMax": 1,
  "schemaMin": 1,   "schemaMax": 1,
  "checkpointToken": "eyJ…",
  "reset": false
}
```

`clientId` is a durable per-installation identifier, distinct from the Jellyfin device id and
access token. `checkpointToken` is omitted when the client has none. `reset: true` abandons any
existing checkpoint and forces a fresh snapshot; it never accompanies a `checkpointToken`.

Body size is capped at 16 KiB. Session creation is rate limited per `(user, client)`.

Response `201`:

```json
{
  "sessionId": "kQ7…",
  "mode": "snapshot",
  "protocolVersion": 1,
  "schemaVersion": 1,
  "cursor": "MXxzfDE3fDQyMDA",
  "checkpointToken": "eyJ…",
  "snapshotGeneration": "17",
  "journalHead": 0,
  "expiresAt": "2026-08-14T15:48:55.123Z",
  "state": "streaming",
  "message": null
}
```

`cursor` is the position to resume from, or omitted/null to start from the beginning. `sessionId`
appears in a URL path and must be path-safe (no `/`, no whitespace).

### 5.3 `GET /AureliaSync/v1/sessions/{sessionId}/stream`

Query parameters: `after` (cursor, omitted on the first call), `maxRecords`, `maxBytes`.

Returns one segment as `application/x-ndjson` (§7). The server may return fewer records than
requested for any reason, and clamps both limits to its own bounds.

**This endpoint must not fail for transient reasons.** The client has no retry and no fallback: any
non-2xx aborts the entire sync. In particular, "the snapshot is still being built" is *not* an
error — see §6.1.

### 5.4 `POST /AureliaSync/v1/sessions/{sessionId}/ack`

```json
{
  "throughCursor": "MXxzfDE3fDQyMDA",
  "clientCommitId": "0f1c…",
  "recordCount": 437
}
```

`throughCursor` is cumulative: everything up to and including that cursor has been durably
committed locally. `recordCount` counts record lines only, excluding `segment.begin` and
`segment.end`. `clientCommitId` is a fresh identifier per segment, reused verbatim on retry.

Response `200`:

```json
{ "checkpointToken": "eyJ…" }
```

Semantics:

- The checkpoint advances **atomically and monotonically**.
- A repeated `clientCommitId` returns the stored result with no further effect.
- A cursor at or below the current checkpoint is an accepted no-op.
- A cursor never issued to this session, or beyond the highest issued, is rejected with `409`.
- The client never acknowledges records merely received into memory.

**Acknowledgement is idempotent independently of session lifetime** — see §6.3. This matters more
than it looks.

### 5.5 `DELETE /AureliaSync/v1/sessions/{sessionId}`

Releases session state and preserves the checkpoint. Returns `204`.

Must be **idempotent and tolerant of unknown session ids**: the client fires it from a deferred
task, so it can arrive after, or concurrently with, the final acknowledgement, and it is also sent
when a session was opened but its stream then failed.

### 5.6 `GET /AureliaSync/v1/sessions/{sessionId}`

Diagnostic only. The client does not call it.

### 5.7 `GET /AureliaSync/v1/admin/diagnostics`

Administrator only (`RequiresElevation`). Aggregates only — never payloads, and never another
user's listening data.

---

## 6. Three rules that are easy to get wrong

### 6.1 A snapshot is streamed while it is still being built

Materialising a 30,000-track library takes on the order of one to three minutes. The client cannot
be made to wait for that, and cannot be told to come back later:

- it has **no retry and no fallback**, so any non-2xx from `/stream` fails the whole sync;
- it uses `URLSession.shared`, so roughly 60 seconds between received packets;
- its drain loop ends **only** when a segment reports `caughtUp: true`, with no zero-record or
  same-cursor guard, so a server that makes no progress spins it.

Therefore `POST /sessions` returns immediately, and `/stream` answers with a **valid but empty
segment** while the snapshot is still being built:

- If the snapshot is not ready, wait server-side for it (bounded well below the client's timeout),
  then return a properly framed segment containing zero records, `caughtUp: false`, and a cursor
  echoing the requested position.
- Once it is ready, serve records normally.
- **Never set `caughtUp` while the snapshot is incomplete.**

> **Clients must therefore tolerate a zero-record segment.** It is not an error and not the end of
> the stream; it means "still working, ask again". A drain loop should keep going on
> `caughtUp: false` regardless of record count.

A snapshot is served only once it is complete. Serving rows as they are materialised was
considered and rejected: album track counts are only known after the tracks are counted, so an
album record streamed early would carry a count that is later corrected, and a client that already
received it would never see the correction.

Because the client promotes its staged catalog only on the `caughtUp` segment, a build that fails
part-way leaves inert staging rows. The next session gets a new generation and re-streams from the
beginning; the client's staging writes are upserts, so replay is safe.

### 6.1a Choosing between snapshot and changes

`POST /sessions` returns `mode: "changes"` only when the client has fully acknowledged a snapshot
**and** its position is still covered by the journal. Otherwise it gets a snapshot, and `state`
carries why:

| Reason | Meaning |
|---|---|
| `newClient` | No checkpoint on record |
| `checkpointExpired` | The client was away long enough that its position was discarded |
| `journalGap` | Records the client still needed were reclaimed |
| `schemaChanged` | The negotiated wire schema differs from the one it last used |

> **A gap is never silently skipped.** Resuming a client from the journal floor when its position
> is below it would leave it believing it was current while missing everything in between. It takes
> a fresh snapshot instead — expensive, and correct.

A position of exactly `floor - 1` is still contiguous: the next record the client needs is the
oldest one retained, so it is served changes rather than forced to resynchronise.

### 6.2 An unknown generation is not an error

The client sends **only** `checkpointToken` when opening a session — never a cursor. The token must
therefore fully encode the resume position, and the server must handle a token whose snapshot is
gone gracefully: return a normal session with a fresh generation and no `cursor`, so the client
starts over.

Reject a token only when it fails its signature, or belongs to a different user or client.

### 6.3 Acknowledgement must outlive its session

The client commits a segment to its database **before** acknowledging it, recording the pending
`clientCommitId` and `sessionId`. After a crash it replays that exact acknowledgement to that exact
session id before opening a new session.

If the session has since expired, the server must still accept it. Look up the receipt by
`(userId, clientId, clientCommitId)` **before** checking session liveness, and return `200` with
the stored `checkpointToken`.

> **Known client defect (report, do not design around):** the client's recovery branch for this
> case matches on HTTP 404, but 404 is intercepted earlier and reported as "plugin not installed",
> so the branch is unreachable and a stale pending acknowledgement hard-fails the sync. The
> server-side rule above avoids the situation entirely; the client should additionally treat `410`
> on a pending-ack replay as "open a new session and replay".

---

## 7. Segment format

`Content-Type: application/x-ndjson`, UTF-8, no BOM, one JSON object per line, `\n` separated.
Responses are gzip-encoded when the client sends `Accept-Encoding: gzip`; Jellyfin's own response
compression does not cover this media type, so the endpoint compresses itself. `Content-Length` is
never set.

**Every line is a JSON object with a string `kind`.** Every record line additionally carries a
`cursor` — including kinds the server treats as metadata. A record line without `cursor` is a
decoding failure that aborts the client's sync.

### 7.1 `segment.begin` — exactly once, before any record

```json
{"kind":"segment.begin","wireSchemaVersion":2,"protocolVersion":1,"sessionId":"kQ7…","mode":"snapshot","generation":17,"afterCursor":null,"serverTime":"2026-08-13T15:48:55.123Z"}
```

### 7.2 Record lines

```json
{"cursor":"MXxzfDE3fDQyMDE","sequence":4201,"kind":"item.upsert","entityType":"track","entityId":"6f3a…","payload":{…}}
```

`sequence`, `entityType` and `entityId` are optional in the framing but should always be sent.

### 7.3 `segment.end` — exactly once, last

```json
{"kind":"segment.end","cursor":"MXxzfDE3fDQyMDE","recordCount":1000,"byteCount":412887,"caughtUp":false,"sessionUpperBound":34512,"stopReason":"maxRecords","nextAfter":"MXxzfDE3fDQyMDE"}
```

`stopReason` ∈ `maxRecords` | `maxBytes` | `timeBudget` | `upperBound` | `clientAbort`.

### 7.4 `error`

```json
{"kind":"error","code":"snapshotInvalidated","message":"…","correlationId":"…"}
```

Only when the response body has already started; before that, fail with an HTTP status and the §4
envelope. An `error` line aborts the client's sync.

### 7.5 Validity

> **A segment is valid only if it ends with a `segment.end` line.**
>
> If the stream ends without one, or contains an `error` line, the client discards the **entire**
> segment and retries from its last acknowledged cursor. Nothing from a discarded segment is
> applied.

This is what makes replay safe and lets the server terminate a response at any point without
coordinating with the client.

### 7.6 Hard limits and invariants

| Rule | Consequence of getting it wrong |
|---|---|
| The byte cap counts **every byte of the body**, including framing, control lines and newlines | The client counts the whole body against `8388608` and aborts. Budget ~7.5 MiB of records |
| `caughtUp: true` appears **exactly once**, on the final segment, which **may carry records** | Never true → the client never promotes and loops forever. True early → the library is truncated to that segment |
| Every segment must make forward progress, or set `caughtUp` | The client's loop has no idle guard and will spin |
| All `playlist.replace` records for one playlist must be in **one segment** | The client deletes that playlist's membership and reinserts only the current segment's rows, truncating the playlist |
| Cursors must be URL- and path-safe | The client interpolates them with `.urlQueryAllowed`, which does not escape `&`, `=`, `+`, `#`. Use base64url **without padding** |
| Never emit a record `kind` outside §8 | Unknown kinds are fatal to the client, not skipped |

---

## 8. Records

### 8.1 Kinds

| Kind | v1 | Meaning |
|---|---|---|
| `item.upsert` | yes | Create or replace one entity; `entityType` ∈ `track` `album` `artist` `playlist` `genre` |
| `playlist.replace` | yes | **One playlist entry**, not a whole playlist (§8.4) |
| `userData.upsert` | yes | Per-user state for one item |
| `item.delete` | changes only | Tombstone. **Never emitted in snapshot mode** — snapshot delivery has no removal channel and the client drops these. In changes mode they must be applied |
| `relationship.replace` | reserved | Accepted and ignored |
| `control.reconcile` | reserved | Accepted and ignored |

### 8.2 Ordering

In **changes** mode records are emitted in journal order — the order the changes happened — and
cursors address journal sequences rather than snapshot positions. The ordering below applies to
snapshot delivery.

Records are emitted in a single global ordinal order:

```
genre → artist → album → track → playlist → playlist entries → userData
```

and within each phase by `entityId` compared as a 32-character hexadecimal ASCII string. That
ordering is collation- and locale-independent and reproducible, so two builds of an unchanged
library produce byte-identical output.

Parents precede children, so a client applying records incrementally never has a dangling
reference. User data comes last, so favourites land on rows that already exist.

### 8.3 Entity payloads

**`artist`**

| Field | Type | Notes |
|---|---|---|
| `id` | string | |
| `name` | string | |
| `sortName` | string? | |
| `biography` | string? | Jellyfin's overview |
| `imageTag` | string? | Primary image cache tag |
| `isAlbumArtist` | bool? | Whether Jellyfin credits this artist on albums rather than only on individual tracks |
| `isFavorite` | bool? | |

The snapshot carries **every** artist, including guests credited only on single tracks, because
track records reference them. Browsing by artist normally wants only album artists, and
`isAlbumArtist` is the only thing in the stream that distinguishes the two — a client that keeps a
separate album-artist list must populate it from this field.

**`album`**

| Field | Type | Notes |
|---|---|---|
| `id`, `name`, `sortName` | | |
| `artistName` | string? | Album artist display name |
| `artistId` | string? | Primary album artist |
| `productionYear` | number? | |
| `genreIDs` | string[]? | |
| `imageTag` | string? | |
| `isFavorite` | bool? | |

**`track`**

| Field | Type | Notes |
|---|---|---|
| `id`, `name`, `sortName` | | |
| `artistName` | string? | Joined display string |
| `artistId` | string? | First credit |
| `artistIDs` | string[]? | **Order is significant** — the client stores the array index as the credit position |
| `albumId` | string? | The album record carries its name and artwork |
| `duration` | number? | **Seconds**, not ticks |
| `indexNumber` | number? | Track number |
| `parentIndexNumber` | number? | Disc number |
| `productionYear` | number? | |
| `genreIDs` | string[]? | |
| `imageTag` | string? | The track's own Primary tag |
| `isFavorite` | bool? | |

**`playlist`**

`id`, `name`, `sortName`, `imageTag`, `dateCreated`, `isFavorite`.

**`genre`**

`id`, `name`. Genres carry no artwork, sort name, or user state.

#### Counts are the client's to derive

No record carries a count of another entity — an album's track count, an artist's or genre's album
count, a playlist's length. Every one of those changes without the entity that would carry it being
touched, so a copy on the wire is a copy that goes stale: the count belongs to whoever can see the
whole set, and after a sync that is the client. It counts its own rows, which also guarantees the
number agrees with the list underneath it.

For the same reason a track names its album only by `albumId`. The album's name and artwork live on
the album record, which is re-sent whenever they change.

#### Artwork is a tag, not a URL

The server sends `imageTag`. **The client composes the URL**:

```
\(baseURL)/Items/\(id)/Images/Primary?maxWidth=\(width)&tag=\(imageTag)
```

The server cannot do this correctly. It does not know which address the client reached it by — LAN
address, reverse proxy, or configured published URL — and `maxWidth` is a client rendering
decision. A URL baked at the server is wrong for every client that connects by a different route.

### 8.4 `playlist.replace` is one record per entry

Each record carries one entry **and the full track payload for that entry**:

```json
{"cursor":"…","kind":"playlist.replace","entityId":"6f3a…","payload":{
  "playlistID":"9c2e…","playlistEntryID":"a71b…","position":0,
  "id":"6f3a…","name":"Song","artistName":"Artist","albumId":"…","duration":213.4,"…":"…"}}
```

The client both inserts the membership row and upserts the track, so the payload must be complete
track metadata, not just an identifier.

`position` is dense and zero-based. **Duplicates must be removed**, keeping the first occurrence
and renumbering: the client's `playlistEntry` primary key is `(playlist, item)`, so a repeated
track collapses to one row.

All entries for one playlist must appear in a single segment (§7.6).

### 8.5 `userData.upsert`

`id` (the item), `isFavorite`, `playCount`, `lastPlayedAt`, `playbackPositionTicks`.

> **Omission never clears a value.** The client applies these with SQL `COALESCE`, so a missing or
> null field leaves the existing value untouched. To un-favourite an item send
> `"isFavorite": false`; to clear a resume position send `"playbackPositionTicks": 0`. Sending
> `null` does nothing.

Records are emitted only for items with something worth sending — favourited, played, or with a
stored position.

User data is scoped to the authenticated user and must never leak across users.

---

## 9. Visibility

Every record is filtered to what the authenticated user may actually see. Library access, parental
rating, and blocked tags all apply, and playlists additionally respect their own ownership and
sharing rules.

Two users of the same server receive different snapshots. A snapshot is never shared between users
in v1.

---

## 10. Client obligations

1. Apply a segment in **one** local transaction, and persist the resulting cursor in that same
   transaction. The local checkpoint must never move ahead of the local data.
2. Acknowledge only after that transaction commits. Never acknowledge data merely received.
3. Treat a segment without a `segment.end` line as if it never arrived.
4. Promote a staged catalog only on the segment reporting `caughtUp: true`.
5. Retry a failed acknowledgement with the **same** `clientCommitId`.
6. Never discard a good local library because a sync failed.
7. Keep `clientId` stable across launches and reinstalls.

---

## 11. Version history

| Version | Status | Notes |
|---|---|---|
| 1 | current | Snapshot and change delivery. `item.delete` is emitted in changes mode; `relationship.replace` and `control.reconcile` remain reserved |

Unknown **optional** fields are ignored, so the server may add them without a version bump. Adding
a record `kind`, changing a key, or changing a field's meaning requires a new wire schema version,
because unknown kinds are fatal to the client.
