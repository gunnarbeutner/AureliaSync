# Open items

Known-open work. Ordered by consequence within each group, not by effort. Items marked
**unverified** are things the code intends to do but nothing has yet proven.

Phase 4 (diagnostics, the Jellyfin 12 lane, the reconciliation fast path, hardening) is complete and
running as **v1.1.0**.

The largest open item is no longer a defect but an absence: **the server and the client have still
never completed a run against each other.** The six-step end-to-end script is written up in
`COMM.md` and needs someone who can drive the app.

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

A restart during a write should leave no gap and no duplicate sequence. The *invariants* are now
checked after every deployment restart — 344 rows spanning 33027–33370 with no duplicate sequence
and no gap, and no snapshot left in `building` — but no restart has yet been deliberately timed to
land inside an append. So the property holds every time it has been looked at; it has not been
adversarially provoked.

---

## Removed in 1.2.3

### Both digests are gone

Neither justified its cost. The segment digest could only detect corruption between hashing and
verification — a window already covered by the `segment.end` framing, gzip's CRC and TLS — and was
structurally incapable of catching a server-side error, having been computed from the same bytes it
would have had to disprove. The snapshot digest was worse: producing it re-read every row of the
finished snapshot on every build, and no code path ever read the result.

An earlier draft of this note claimed removing them lost an automatic regression check. That was
wrong, and worth correcting rather than quietly fixing: **nothing was checking automatically before
either.** The snapshot digest was computed on every build and compared against nothing. What existed
was a manual affordance, used exactly once — to prove `FastHydration` byte-neutral.

Payload stability belongs in tests, and already is there: `docs/fixtures/*.ndjson` are byte-exact
golden files generated from the real wire types, and `FixtureTests` fails on any drift. That covers
every record kind, costs nothing on the wire, and catches changes a runtime digest never could,
because it compares against a *previous* known-good output rather than against itself.

The one boundary tests cannot reach is Jellyfin's own hydration — `Jellyfin.Server.Implementations`
is not published to NuGet, so `BaseItemRepository.DeserializeBaseItem` cannot be driven from a unit
test. That specific assumption behind `FastHydration` is only checkable against a real server, by
building twice with the flag flipped and diffing. Worth doing on a Jellyfin upgrade, not on a timer.

`M006DropChecksums` drops the three columns. The per-record one was never populated at all.

## Introduced in 1.2.0

### Where the build time actually goes — measured, not guessed

Reading Jellyfin's `BaseItemRepository` settled this. Per row it runs:

```csharp
if (TypeRequiresDeserialization(type) && baseItemEntity.Data is not null && !skipDeserialization)
    dto = JsonSerializer.Deserialize(baseItemEntity.Data, type, JsonDefaults.Options) as BaseItemDto;
...
return Map(baseItemEntity, dto, appHost, logger);   // always runs, from columns
```

`TypeRequiresDeserialization` is false for types carrying `RequiresSourceSerialisation` —
`MusicAlbum`, `MusicArtist` and `MusicGenre` all do. **`Audio` does not**, so tracks, and only
tracks, paid a `JsonSerializer.Deserialize` each. On this library that is 30,224 parses whose result
`Map` then overwrites from database columns, artist and genre credits included:

```csharp
hasArtists.Artists = entity.Artists?.Split('|', StringSplitOptions.RemoveEmptyEntries) ?? [];
dto.Genres = string.IsNullOrWhiteSpace(entity.Genres) ? [] : entity.Genres.Split('|');
```

`InternalItemsQuery.SkipDeserialization` turns it off. Two consecutive builds of the same library,
no restart between them, produced **the same digest over all 43,343 records** — so the payloads are
byte-identical, proven rather than argued — at **89 s with it against 123 s without**.

Shipped as `FastHydration`, on by default. It stays a switch because the guarantee depends on
Jellyfin's internals: if a release ever moves a field out of the columns into the blob alone, this
is the way back. The digest that proved it has since been removed, so such a regression would not be
caught automatically — see *Both digests are gone* above.

### Build time is noisy and the earlier comparison was worthless

Two builds with identical settings measured 123 s and 205 s. Any claim resting on a single pair of
timings on this server is unsound, including the one this file previously made about progressive
delivery causing a regression — that comparison did not survive the noise. Time a change by building
back to back under the same conditions, as the hydration test above did, or not at all.

### The superseded worry, kept because the reasoning was wrong in an instructive way

Progressive delivery made the client-visible wait collapse — first record in 0.5 s instead of ~100 s,
and no empty segments at all. But the *build* itself now measures 205 s clean, against 97–128 s for
the earliest builds under the old code.

That is not a clean comparison. Old-code builds ranged 97 s to 282 s and new-code builds 205 s to
276 s, so the ranges overlap and server load plainly dominates. The plausible new cost is one extra
small write per hydration batch to publish the watermark — perhaps a few seconds, not eighty. It has
not been isolated.

Worth measuring properly on an otherwise idle server before concluding anything. If it is real, the
watermark could be published every N batches instead of every batch, which costs a little delivery
smoothness and nothing else.

### Repair mode has never run

`journalGap` now triggers a repair — changed items plus a manifest of surviving identifiers — rather
than a full snapshot. Every piece of it is exercised by unit tests and it is deployed, but **no
client has fallen out of retention since**, so the path has never executed end to end.

It also rests on two assumptions worth restating:

- **`DateLastSaved` moves for every change that matters.** Same assumption as reconciliation's fast
  path, and we already know one counterexample — playlist membership. Playlists are therefore always
  sent in full during a repair.
- **The manifest is applied at promotion, not per record.** A client that prunes against a partially
  received manifest deletes most of its library. This is stated in `COMM.md`; it is the single most
  dangerous thing about the design.

`EnableGapRepair` turns it off and restores the old full-snapshot behaviour if either assumption
turns out to be wrong.

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

### Reconciliation's fast path trusts `DateLastSaved` — *(fixed in 1.1.0, with a caveat)*

The nightly pass no longer re-projects the whole library: it asks Jellyfin for items saved since a
stored watermark and carries the rest forward untouched. On this library that is **152s → 12s**,
33,003 items skipped, zero spurious tombstones.

The caveat is what the fast path now depends on. An item whose content changes *without* Jellyfin
moving `DateLastSaved` is invisible to a fast pass. Three mitigations, all deliberate:

- the watermark rewinds a minute on read, so an item saved during the previous pass is re-examined;
- **playlists are always fully compared**, because membership changes move no timestamp — this is
  precisely why reconciliation exists for them;
- a seeding pass (empty inventory) still examines everything.

A periodic full pass — say weekly, ignoring the watermark — would close the gap entirely. Not done
because nothing has yet been observed to slip through it.

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

### The Jellyfin 12 lane compiles but has never been run — and is deliberately unpublished

The plugin now multi-targets `net9.0;net10.0`, and CI builds, analyses and stray-assembly-checks
both. Jellyfin 12.0.0-rc5's API turned out to be a clean recompile: no source change was needed.

**Compiling is the entire test.** No Jellyfin 12 container has been run against it, by decision.
The 12.0 matrix entry in `release.yml` is therefore left commented out, because Jellyfin selects the
highest version whose `targetAbi` it satisfies — publishing a `2.x` build would actively steer every
12.0 server onto something nobody has ever executed. Publishing nothing leaves a 12.0 server with no
compatible version, which is a visible absence rather than a silent breakage.

To ship it: uncomment the matrix entry and tag `v2.0.0`. Do that only once a 12.0 server exists to
try it on, and expect to re-cut the lane at GA — 12.0 is still a release candidate and its API may
still move.

Related, and now the more pressing half: `/docker/jellyfin/docker-compose.yaml` uses
`image: jellyfin/jellyfin` with no tag, so a `docker compose pull` floats to `:latest`. When 12.0
goes stable that pull will upgrade the server and the installed 1.x plugin will fail to load. Pin
the image.

### `libe_sqlite3` on the server is affected by CVE-2025-6965

Not our dependency and not shipped by us — `ExcludeAssets=runtime` means the plugin contains exactly
one DLL and resolves SQLite from Jellyfin's own copy — but that copy is what the plugin runs on.
Jellyfin's bundled `libe_sqlite3.so` dates from September 2024 and predates the fix; SQLitePCLRaw
has no patched release. The .NET 10 SDK's audit database flags it at build time, which is why the
csproj carries a `NuGetAuditSuppress` for that one advisory URL (rather than a blanket `NU1903`, so
any *other* advisory still fails the build).

Nothing to do in this repository. It is recorded here because the suppression would otherwise look
like something being swept under the rug, and because it resolves when Jellyfin updates its runtime.

---

## Notes for the client (see `COMM.md`)

Everything originally asked of the client has been reported done: `imageTag`/`albumImageTag` artwork
composition, `isAlbumArtist`, `410`-on-pending-ack recovery, zero-record segment tolerance,
`item.delete` in change mode, and a ten-minute idle guard on the drain loop. All of it is verified
only by the client agent's own account — none of it has been observed from this side.

Still to hand over:

1. **Log the new `reason` field** on a session response (`newClient`, `checkpointExpired`,
   `journalGap`, `schemaChanged`, `snapshotIncomplete`, `clientRequested`). It turns "it resynced
   again" into something diagnosable from a client log alone.
2. **Run the six steps in `COMM.md`.** That is the outstanding item, not a nicety.

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

### Resolved in 1.1.0

Storage pressure and the rate limit were both listed here as deferred; both are now implemented and
exercised on the live server.

The rate limit was also **wrong**, and blocked a real client. It capped session *creation* at six
per hour, but a client that syncs on launch, on foreground, on pull-to-refresh and on a timer
legitimately opens six sessions in two minutes — the observed failure was a well-behaved client
being throttled for behaving normally. The limit now sits on snapshot *builds*
(`MaxSnapshotBuildsPerUserPerHour`, default 4), which is the operation that actually costs
something; ordinary and change sessions are never throttled. Verified: ten rapid sessions all
succeed, and the third forced rebuild in an hour returns `429`.

The lesson generalises. A limit belongs on the expensive operation, not on the request that might
lead to one.
