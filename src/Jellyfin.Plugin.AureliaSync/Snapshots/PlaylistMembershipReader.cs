using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AureliaSync.Projection;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.AureliaSync.Snapshots;

/// <summary>
/// Reads a playlist's visible audio membership.
/// </summary>
/// <remarks>
/// Shared by the snapshot builder and the change journal on purpose. The rules below are subtle
/// enough that two copies would drift, and a drift would show up as a playlist that differs
/// depending on whether the client arrived at it by snapshot or by delta.
/// </remarks>
public static class PlaylistMembershipReader
{
    /// <summary>
    /// Reads the entries a user can see, deduplicated and densely renumbered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Duplicates are dropped keeping the first occurrence. The client's membership table is keyed
    /// on (playlist, item), so a repeated track would collapse there anyway — sending it would only
    /// make the stated track count unreachable.
    /// </para>
    /// <para>
    /// The entry identifier matches what Jellyfin's own playlist endpoint reports, which is the
    /// item's identifier rather than a per-entry handle: Jellyfin has no stable per-entry identity
    /// to offer.
    /// </para>
    /// </remarks>
    /// <param name="playlist">The playlist.</param>
    /// <param name="user">The user whose visibility applies.</param>
    /// <param name="userId">That user's identifier.</param>
    /// <param name="reader">Reads Jellyfin entities into facts.</param>
    /// <param name="albumIds">Known album identifiers, to avoid walking parent chains.</param>
    /// <param name="knownTracks">Already-read facts, reused when present.</param>
    /// <returns>The visible entries in order.</returns>
    public static IReadOnlyList<PlaylistEntryFacts> Read(
        Playlist playlist,
        User user,
        Guid userId,
        BaseItemFactsReader reader,
        IReadOnlySet<Guid>? albumIds = null,
        IReadOnlyDictionary<Guid, ItemFacts>? knownTracks = null)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(reader);

        var entries = new List<PlaylistEntryFacts>();
        var seen = new HashSet<Guid>();

        foreach (var (link, item) in playlist.GetManageableItems())
        {
            if (item is not Audio || !item.IsVisible(user, false) || !seen.Add(item.Id))
            {
                continue;
            }

            var facts = knownTracks is not null && knownTracks.TryGetValue(item.Id, out var known)
                ? known
                : reader.Read(item, userId, albumIds);

            entries.Add(new PlaylistEntryFacts(
                facts,
                link.ItemId?.ToString("N", CultureInfo.InvariantCulture),
                entries.Count));
        }

        return entries;
    }
}
