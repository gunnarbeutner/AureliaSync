# AureliaSync

A Jellyfin server plugin that gives the [Aurelia](https://github.com/gunnarbeutner/Aurelia) music
client a resumable, ordered, idempotent library synchronisation protocol.

It replaces Aurelia's repeated offset-based crawls over artists, albums, tracks, playlists, genres
and playlist contents with a durable change journal and per-client checkpoints. The stock Jellyfin
API remains Aurelia's fallback when the plugin is absent or unhealthy.

The full design specification lives in [`PLAN.md`](PLAN.md).

| | |
|---|---|
| **Plugin GUID** | `3fbf911d-ab0c-46dc-81d6-b3317bb8b176` |
| **Repository manifest** | `https://gunnarbeutner.github.io/AureliaSync/manifest.json` |
| **Supported server** | Jellyfin 10.11.x (`targetAbi 10.11.0.0`, net9.0) |
| **Licence** | GPL-3.0-only |

## The plugin is read-only with respect to Jellyfin

This is the property that makes installing and rolling back safe, so it is stated up front and
enforced by review: **AureliaSync never writes to Jellyfin's database and never calls a mutating
Jellyfin API.**

The complete set of Jellyfin calls it makes is:

- `ILibraryManager.GetItemIds` / `GetItemList` / `GetArtists` / `GetAlbumArtists` / `GetMusicGenres` / `GetMusicGenreId`
- `IUserManager.GetUserById`
- `IImageProcessor.GetImageCacheTag`
- read-only subscriptions to `ILibraryManager.ItemAdded/ItemUpdated/ItemRemoved` and
  `IUserDataManager.UserDataSaved` (from Phase 3 onward)

All plugin state lives in its own SQLite database under the Jellyfin **data** directory
(`<DataPath>/aureliasync/aureliasync.db`) — deliberately *not* under `plugins/`, so it survives
plugin upgrades and is never touched by Jellyfin's plugin-directory cleanup.

Uninstalling the plugin leaves the server exactly as it was found. The database file is left on disk
so that reinstalling resumes existing client checkpoints rather than forcing a fresh snapshot.

## Version lanes

Jellyfin selects a plugin release with
`versions.Where(targetAbi <= appVersion).OrderByDescending(VersionNumber).First()` — the **highest
version number** among ABI-compatible entries wins. A newer server would therefore select an older,
incompatible build and fail to load it. Releases are consequently partitioned into lanes:

| Lane | Major versions | `targetAbi` | TFM | Jellyfin |
|---|---|---|---|---|
| Current | `0.x`, `1.x` | `10.11.0.0` | net9.0 | 10.11.x |
| Next | `2.x` | `12.0.0.0` | net10.0 | 12.x *(not yet shipped)* |

**Every version in a lower lane must sort below every version in a higher one.** The release
workflow refuses a tag whose major version does not belong to the lane being built.

## HTTP API

All endpoints live under `/AureliaSync/v1` and require normal Jellyfin authentication (the standard
`Authorization: MediaBrowser Token="…"` header). Endpoints marked administrative additionally
require the `RequiresElevation` policy.

User identity is derived **exclusively** from the authenticated principal. A request body may never
select a user scope, and API-key authentication is rejected because it carries no user scope.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/status` | Capability probe: versions, health, journal head/floor, whether this user needs a snapshot. Cheap — runs no library queries |
| `POST` | `/sessions` | Open or resume a delivery session |
| `GET` | `/sessions/{id}` | Materialisation and delivery progress |
| `GET` | `/sessions/{id}/stream` | Bounded NDJSON segment after an opaque cursor |
| `POST` | `/sessions/{id}/ack` | Advance the durable per-client checkpoint |
| `DELETE` | `/sessions/{id}` | Release session state, preserving the checkpoint |
| `POST` | `/subscription/reset` | Abandon the checkpoint and request a fresh snapshot |
| `GET` | `/admin/diagnostics` | Administrator observability *(admin only)* |

### Streaming contract

A segment is a bounded NDJSON response. The first line is `segment.begin`, the last is `segment.end`,
and every line between them is one record envelope.

> **A segment is valid only if it ends with a `segment.end` line.**
>
> If the stream ends without one, or contains a line of kind `error`, the client must discard the
> **entire** segment and retry from its last acknowledged cursor. Nothing in a discarded segment may
> be applied.

This rule is what makes replay safe: a client never commits a partially received segment, and the
server is free to terminate a response at any point (record limit, byte limit, time budget, or
failure) without coordinating with the client.

Acknowledgement is cumulative and idempotent. Repeating an acknowledgement returns the stored result
with no side effects; a lower cursor is an accepted no-op; a cursor that was never issued to the
session is rejected. Clients acknowledge only what they have durably committed locally — never data
merely received into memory.

## Building

Requires the .NET 9 SDK.

```bash
dotnet build AureliaSync.sln -c Release -warnaserror
dotnet test tests/Jellyfin.Plugin.AureliaSync.Tests
```

The plugin must ship **only** its own assembly. Jellyfin's own libraries and `Microsoft.Data.Sqlite`
are referenced with `ExcludeAssets=runtime` and resolved at runtime from the server, because loading
a second copy of `SQLitePCLRaw` into the plugin's `AssemblyLoadContext` would duplicate a
process-global native provider registration. CI fails the build if any framework assembly appears in
the output.

## Installing

1. Jellyfin Dashboard → Plugins → Repositories → Add
   `https://gunnarbeutner.github.io/AureliaSync/manifest.json`
2. Catalog → **Aurelia Sync** → Install
3. Restart Jellyfin — plugin API routes are registered at startup, so they do not exist until then.

To roll back: Dashboard → Plugins → Aurelia Sync → Uninstall → restart. The database file is left in
place; delete `<DataPath>/aureliasync/` by hand if you want a clean slate.
