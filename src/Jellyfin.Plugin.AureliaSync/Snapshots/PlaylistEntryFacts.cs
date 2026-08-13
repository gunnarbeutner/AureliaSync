using Jellyfin.Plugin.AureliaSync.Projection;

namespace Jellyfin.Plugin.AureliaSync.Snapshots;

/// <summary>
/// One playlist entry: the track, Jellyfin's identifier for the membership, and its position.
/// </summary>
/// <param name="Facts">The track at this position.</param>
/// <param name="EntryId">
/// Jellyfin's membership identifier, which matches what its own playlist endpoint reports — the
/// item's identifier rather than a per-entry handle, because Jellyfin has no stable per-entry
/// identity to offer.
/// </param>
/// <param name="Position">Zero-based, dense position.</param>
public readonly record struct PlaylistEntryFacts(ItemFacts Facts, string? EntryId, int Position);
