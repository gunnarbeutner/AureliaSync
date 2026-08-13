# Conformance fixtures

Recorded NDJSON segments for testing a client decoder without a live server or a built snapshot.

These files are **generated from the server's own wire types**, not hand-written, and a test
regenerates and compares them on every build. They therefore cannot drift from the implementation:
if the wire format changes, the diff shows up here in the same commit.

See [`../PROTOCOL.md`](../PROTOCOL.md) for the contract these illustrate.

| File | What it is | What it should exercise |
|---|---|---|
| `snapshot-complete.ndjson` | A whole tiny library in one segment | Every record kind and entity type, ending `caughtUp: true` — the signal to promote a staged catalog |
| `snapshot-partial.ndjson` | A mid-drain segment | `caughtUp: false`, so the client must request again and must **not** promote |
| `stream-error.ndjson` | A segment that fails after the body started | No `segment.end`, so the whole segment must be **discarded** and retried from the last acknowledged cursor |

## What `snapshot-complete.ndjson` contains

```
segment.begin
item.upsert   genre     Jazz
item.upsert   artist    Björk
item.upsert   album     Homogenic
item.upsert   track     Hunter
item.upsert   track     Jóga
item.upsert   playlist  Evening
playlist.replace        Jóga    position 0
playlist.replace        Hunter  position 1
userData.upsert         (Hunter: favourited, 12 plays)
userData.upsert         (Jóga: not favourited, resume position set)
segment.end   caughtUp: true
```

Things worth asserting against it:

- **Ordering** is genre → artist → album → track → playlist → playlist entries → user data. Parents
  precede children, so applying records in order never leaves a dangling reference, and user data
  arrives last so favourites land on rows that already exist.
- **`playlist.replace` is one record per entry**, each carrying the full track payload — the client
  inserts the membership row and upserts the track from the same record. Playlist order here is
  deliberately *not* the album order, so a decoder that ignores `position` will visibly disagree.
- **Positions are dense and zero-based**, and `playlist.trackCount` equals the number of entries
  actually sent.
- **Non-ASCII is raw UTF-8**, never `\u` escapes — `Björk` and `Jóga` are in there to catch a
  decoder that assumes ASCII.
- **Dates carry exactly three fractional digits and a `Z`.** This is the most likely silent interop
  break: .NET's default format emits seven digits, which `ISO8601DateFormatter` rejects outright.
- **User data states cleared values explicitly** (`isFavorite: false`, `playCount: 0`) rather than
  omitting them, because the client applies them with `COALESCE` and an omitted field would leave
  the previous value in place.
- **Every line is a JSON object with a string `kind`**, and every record line carries a `cursor` —
  including kinds the server treats as metadata.

## Regenerating

```bash
AURELIASYNC_UPDATE_FIXTURES=1 dotnet test tests/Jellyfin.Plugin.AureliaSync.Tests
```

Only after a deliberate protocol change. Commit the result, update `../PROTOCOL.md`, and say so in
the coordination notes so the client side is not silently left behind.
