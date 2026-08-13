# Open items

Known-open work. Ordered by consequence within each group, not by effort. Items marked
**unverified** are things the code intends to do but nothing has yet proven.

Phase 3 (change journal, change sessions, reconciliation) is complete and running as **v1.0.0**.

---

## Unverified because verifying would damage real data

These paths are covered by unit tests and by the published fixtures, but have never run against a
real library, because doing so means destroying or editing something on a live server. They are the
most likely place for a Phase 2-style surprise — Phase 2's only real bug was found precisely by
running against real data.

### Tombstones have never been delivered end to end

`item.delete` is emitted when Jellyfin raises `ItemRemoved`, and by reconciliation for anything that
disappeared while the plugin was stopped. Proving it needs a track actually deleted from the
library. **Do this on a scratch server, or on a throwaway track**, not on the real one.

Worth checking when it happens: the tombstone is *broadcast* rather than visibility-filtered, so it
should reach every subscribed client, and a client that never held the track should apply it as a
no-op.

### Metadata edits have never produced a delta

An album rename should produce one `item.upsert` whose payload is byte-identical to what a fresh
snapshot would emit for that album. That equality is the whole basis for a client applying deltas
and snapshots interchangeably, and it is currently only asserted by construction — both paths call
the same projector — not by observation.

### Playlist delivery remains entirely unexercised

The target server has no playlists, so `playlist.replace`, duplicate collapsing, dense renumbering,
and the never-split-a-playlist rule have only ever run in tests. The first server with playlists is
effectively the first real test. Ideally test with a playlist containing a repeated track, and one
large enough to cross a segment boundary.

### Restart mid-journal-write

A restart during a write should leave no gap and no duplicate sequence. A restart has been done
many times, but never timed to land inside a journal append, so this is untested rather than known
good.

---

## Correctness gaps

### Two-user visibility is still unverified — and now matters more

Unchanged from Phase 2, but the stakes went up: with per-user journal fan-out, the visibility
decision is now made on *every change*, not once per snapshot. A mistake leaks one user's library
metadata into another's stream continuously rather than once.

`LibraryEnumerator` is written to avoid the `AddUserToQuery` trap and re-checks `IsVisible`, and
`JournalWriter` filters per user through the same path — but nothing has proven it. Needs a second
Jellyfin account with access to a subset of libraries. Deliberately skipped rather than creating an
account on the live server.

### Reconciliation seeding can mask real drift

An empty inventory is treated as "first run": the pass records a baseline and journals nothing,
because journalling a repair per item would hand the client the entire catalog as deltas.

That is right for a client that just took a snapshot, and wrong for a client that already had an old
snapshot when the inventory was empty — for instance immediately after upgrading to the version that
introduced the inventory. Such a client silently misses whatever drifted before the baseline was
taken, until something else forces a resnapshot.

**The proper fix is for the snapshot builder to seed the inventory as it materialises**, so the
inventory is never empty for a user who has a snapshot and the ambiguity disappears. Deferred
because it means threading inventory writes through the build.

---

## Cost and noise

### Reconciliation re-projects the whole library on every run

Each nightly pass enumerates and projects every album, track, artist and genre for every subscribed
user — about 34,500 items here — to compare payload hashes. It took roughly a minute per user and
produced no journal records once seeded, which is the desired outcome, but the cost is paid whether
or not anything changed.

If it becomes a problem, compare `DateLastSaved` first and only project items whose timestamp moved.
The `inventory.observed_revision` column exists for exactly this and is currently written as null.

### Every item update is journalled, including image-only changes

`ItemUpdateType` is ignored, so a metadata refresh that only changes artwork still produces a
record. Harmless — the payload hash would be identical and the client's upsert is idempotent — but
it inflates the journal during a library scan. Filtering on `UpdateReason` would reduce it.

### A change session can end in state `snapshotComplete`

Cosmetic. The acknowledgement path sets that state when a session catches up, which reads oddly for
a change session. Nothing depends on it.

---

## Distribution

### The repository install path has never been exercised on a real server

Every build has been sideloaded. `v1.0.0` is published, the manifest is live at
`https://gunnarbeutner.github.io/AureliaSync/manifest.json`, and CI verifies the published asset's
MD5 against the manifest entry — but nobody has actually installed it through Jellyfin's plugin
catalogue.

Not done because Jellyfin's repository API **replaces** the repository list rather than appending,
so adding ours means rewriting the server's existing list. The current list, for whoever does it:

| Name | URL |
|---|---|
| Jellyfin Stable | `https://repo.jellyfin.org/files/plugin/manifest.json` |
| AudioMuse-AI | `https://raw.githubusercontent.com/NeptuneHub/audiomuse-ai-plugin/master/manifest.json` |
| NoriSync | `https://git.missen.ca/Esmond/jellyfin-plugin-norisync/releases/download/manifest/manifest.json` |

### The Jellyfin image is untagged

`/docker/jellyfin/docker-compose.yaml` uses `image: jellyfin/jellyfin` with no tag, so a
`docker compose pull` floats to `:latest`. Jellyfin 12.0 exists as a release candidate and targets
net10.0; a 12.0 server would *select* this 10.11 build (because `10.11.0.0 <= 12.0`) and then fail
to load it. The version-lane rule in `README.md` guards the manifest side, but the server would
still end up with a broken plugin until a 2.x lane exists.

---

## Notes for the client (see `COMM.md`)

1. **`artworkURL` → `imageTag`** — required, breaking.
2. **`isAlbumArtist`** — the album-artist marker table has no other source under this transport.
3. **Treat `410` on a pending-ack replay as "reopen and replay"**; the `404` branch is unreachable.
4. **Tolerate zero-record segments** — that is how "snapshot still building" is reported.
5. **Apply `item.delete` in change mode.** Dropping them in snapshot mode is correct; dropping them
   in change mode leaves deleted tracks in the library permanently.
6. **Consider an idle guard on the drain loop**, which currently ends only on `caughtUp: true`.

### Observation, not a defect

Track `sortName` arrives as Jellyfin's computed form, e.g. `"0001 - 0001 - Age of Suffering"`.
Correct for ordering within an album; odd if tracks are ever sorted globally by it.

---

## Deferred by design

Considered and postponed deliberately, so they are not mistaken for oversights:

- **Per-record checksums** (`?checksums=record`). Roughly 20% of wire size to duplicate what TLS and
  the per-segment digest already cover. The segment digest is now emitted on every segment.
- **Long polling** for the caught-up case.
- **Cross-user snapshot sharing.** Needs a provably correct visibility key; a bug there is a data
  leak, and per-user storage is not a problem at this scale.
- **`relationship.replace` and `control.reconcile`.** Reserved in the protocol, unused —
  relationships travel on the item payloads as `artistIDs` and `genreIDs`.
- **Storage-pressure handling and rate-limit verification.** The rate limit is implemented
  (six sessions per client per hour, `429` with `Retry-After`) but untested.
