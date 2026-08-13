# Open items

Known-open work, carried forward from phase 2. Ordered by consequence within each group, not by
effort. Items marked **unverified** are things the code intends to do but nothing has yet proven.

---

## Correctness gaps

### Two-user visibility test is unverified — highest risk in the project

`LibraryManager.AddUserToQuery` only applies library-access filtering when the query sets none of
`ItemIds`, `ParentId` or `AncestorIds`. Enumerating identifiers and then hydrating them by
`ItemIds` therefore performs the access check on the first query and silently skips it on the
second. `LibraryEnumerator` is written to avoid this and re-checks `IsVisible` per item, but that
is an argument, not evidence.

**Needs:** a second Jellyfin user with access to a subset of libraries (or none), a snapshot built
for them, and assertions that the row count differs and that none of the first user's favourites
appear. Requires creating a user on the server, so it has not been done.

Until this passes, treat cross-user isolation as designed-for but untested.

### `aggregateChecksum` is documented but never emitted

`docs/PROTOCOL.md` §7.3 lists it on `segment.end` and describes segment-level digests as the
default, but `NdjsonSegmentWriter` leaves it unset, so the field is absent from every segment. The
client round-trips whatever it receives without verifying, so nothing is currently broken — but the
document describes a guarantee the server does not provide.

Either compute it (SHA-256 over the segment's payload bytes, cheap since they are already in hand)
or amend the document. Computing it is preferable: it is the only end-to-end integrity check the
protocol has, and the acknowledgement already carries a field for it.

### Playlist delivery is entirely unexercised on real data

The target server has no playlists, so `playlist.replace`, duplicate collapsing, dense
renumbering, and the never-split-a-playlist rule are covered only by unit tests and fixtures. The
first server with playlists is effectively the first real test.

**Needs:** a playlist on a test server, ideally one with a repeated track and one large enough to
cross a segment boundary.

---

## Distribution

### The published manifest is stale

GitHub Pages advertises **0.1.0.0** while **0.2.1.0** is what actually runs. Every phase-2 build
has been sideloaded, so the release path has not been exercised since v0.1.0.

Cut a v0.2.x release so the repository install path stays honest, and confirm the manifest checksum
verification step still passes with the `.pdb` now included in the package.

---

## Ergonomics

- **Three configuration properties are not editable from the plugin's config page**:
  `StreamWaitSeconds`, `MaxSessionsPerClientPerHour`, `MaxConcurrentSnapshotBuilds`. They are
  settable only by editing the plugin XML by hand.
- **No administrator-triggered rebuild.** Forcing a fresh snapshot currently requires a client to
  send `reset: true`. A button on the config page, or an `IScheduledTask`, would make support
  easier.
- **Rate limiting is unverified.** Six sessions per client per hour is implemented and returns
  `429` with `Retry-After`, but nothing has tested it.
- **`SnapshotBuilder._libraryManager` is dead.** Assigned in the constructor, then only used to
  build the `LibraryEnumerator`. Remove the field.

---

## Notes for the client (see `COMM.md`)

Tracked in detail in the coordination file; repeated here so they are not lost if that file is:

1. **`artworkURL` → `imageTag`** — required, breaking. The server sends image tags; the client
   composes URLs.
2. **`isAlbumArtist`** — the client's album-artist marker table has no other source under the
   plugin transport, so artist browsing would regress to listing every guest credit.
3. **Treat `410` on a pending-ack replay as "reopen and replay".** The client's recovery branch
   matches on `404`, which `validate()` has already turned into "plugin not installed", so the
   branch is unreachable. The server now answers a replayed ack for a dead session with `200`, so
   the common case is covered, but the client-side branch is still wrong.
4. **Tolerate zero-record segments** — they are how "snapshot still building" is reported.
5. **Consider an idle guard on the drain loop.** It terminates only on `caughtUp: true` with no
   check for lack of progress, so a server bug would spin it.

### Observation, not a defect

Track `sortName` arrives as Jellyfin's computed form, e.g. `"0001 - 0001 - Age of Suffering"`.
That is correct for ordering within an album and is what Jellyfin itself uses, but it sorts oddly
if the client ever orders tracks globally by sort name.

---

## Deferred by design

Recorded so they are not mistaken for oversights. Each was considered and postponed deliberately:

- **Per-record checksums** (`?checksums=record`). Roughly 20% of wire size to duplicate what TLS
  and a segment digest already cover.
- **Long polling** for the caught-up case.
- **Cross-user snapshot sharing.** Needs a provably correct visibility key; a bug there is a data
  leak, and 12 MB per user is not worth that risk.
- **`item.delete`, `relationship.replace`, `control.reconcile`.** Reserved in the protocol,
  unused in v1.
- **`changes` mode and the change journal** — the substance of phase 3. Until it lands, every sync
  re-sends the whole catalog, which is slower than the client's existing timestamp-based sync.
