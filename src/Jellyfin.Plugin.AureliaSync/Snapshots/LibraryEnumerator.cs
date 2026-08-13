using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Snapshots;

/// <summary>
/// Reads a user's visible music library out of Jellyfin.
/// </summary>
/// <remarks>
/// <para>
/// <b>The visibility rule this class exists to enforce.</b> Jellyfin applies library-access
/// filtering in <c>LibraryManager.AddUserToQuery</c>, which populates <c>TopParentIds</c> from the
/// user's views — but only when the query sets none of <c>ItemIds</c>, <c>ParentId</c>,
/// <c>AncestorIds</c>, <c>ChannelIds</c>, <c>TopParentIds</c>,
/// <c>AncestorWithPresentationUniqueKey</c> or <c>SeriesPresentationUniqueKey</c>. Setting any of
/// them silently skips access filtering altogether.
/// </para>
/// <para>
/// The obvious implementation — enumerate identifiers, then hydrate them in batches by
/// <c>ItemIds</c> — therefore performs the access check on the first query and quietly bypasses it
/// on the second. That is a cross-library data leak, not a performance detail. This class keeps the
/// two phases separate and never lets a caller supply the query, so the rule cannot be broken from
/// outside; hydration additionally re-checks <c>IsVisible</c> per item.
/// </para>
/// <para>
/// Note that parental rating and tag filters travel differently: <c>InternalItemsQuery(user)</c>
/// calls <c>SetUser</c>, which copies them onto the query itself, so those <i>are</i> applied even
/// on an <c>ItemIds</c> query. It is specifically library access that depends on the guard above.
/// </para>
/// </remarks>
public sealed class LibraryEnumerator
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryEnumerator"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">Logger.</param>
    public LibraryEnumerator(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Orders identifiers the way the protocol requires.
    /// </summary>
    /// <remarks>
    /// By the 32-character hexadecimal form compared ordinally: collation-independent,
    /// locale-independent, stable across Jellyfin versions, and reproducible in a test — which is
    /// what lets two builds of an unchanged library produce an identical checksum.
    /// </remarks>
    /// <param name="ids">Identifiers to order.</param>
    /// <returns>The identifiers in protocol order.</returns>
    public static IReadOnlyList<Guid> InProtocolOrder(IEnumerable<Guid> ids) =>
        ids.OrderBy(id => id.ToString("N"), StringComparer.Ordinal).ToList();

    /// <summary>
    /// Enumerates the identifiers of every item of a kind the user can see.
    /// </summary>
    /// <remarks>
    /// The query deliberately sets no identifier or parent filter, so that Jellyfin populates
    /// <c>TopParentIds</c> from the user's accessible libraries. Do not add one.
    /// </remarks>
    /// <param name="user">The user whose visibility applies.</param>
    /// <param name="kind">The item kind to enumerate.</param>
    /// <returns>Access-filtered identifiers, in protocol order.</returns>
    public IReadOnlyList<Guid> EnumerateIds(User user, BaseItemKind kind)
    {
        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false)
            {
                EnableImages = false,
                EnableUserData = false
            }
        };

        var ids = _libraryManager.GetItemIds(query);
        _logger.LogDebug("AureliaSync: enumerated {Count} {Kind} for the snapshot", ids.Count, kind);

        return InProtocolOrder(ids);
    }

    /// <summary>
    /// Loads a batch of items by identifier, with images and user data attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>EnableUserData</c> is what makes bulk projection affordable: the repository eagerly
    /// includes each item's user-data rows in the same query, so per-item calls to
    /// <c>IUserDataManager.GetUserData</c> — one database round trip each, plus eviction pressure
    /// on the server's shared cache — are never needed.
    /// </para>
    /// <para>
    /// The <c>IsVisible</c> re-check is defensive. These identifiers came from an access-filtered
    /// enumeration, so it should never remove anything; if it ever does, the enumeration was wrong
    /// and the alternative was leaking the item.
    /// </para>
    /// </remarks>
    /// <param name="user">The user whose visibility applies.</param>
    /// <param name="ids">Identifiers to load. Keep batches small; 30,000 items at once is gigabytes.</param>
    /// <returns>The visible items.</returns>
    public IReadOnlyList<BaseItem> Hydrate(User user, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<BaseItem>();
        }

        var query = new InternalItemsQuery(user)
        {
            ItemIds = ids.ToArray(),
            DtoOptions = new DtoOptions(true)
            {
                EnableImages = true,
                EnableUserData = true
            }
        };

        var items = _libraryManager.GetItemList(query);
        var visible = items.Where(i => i.IsVisible(user, false)).ToList();

        if (visible.Count != items.Count)
        {
            // Only reachable if enumeration and hydration disagree, which would mean the rule in
            // this class's remarks had been broken somewhere.
            _logger.LogWarning(
                "AureliaSync: {Hidden} hydrated item(s) failed the visibility re-check and were dropped",
                items.Count - visible.Count);
        }

        return visible;
    }

    /// <summary>
    /// Splits identifiers into batches for hydration.
    /// </summary>
    /// <param name="ids">Identifiers.</param>
    /// <param name="batchSize">Items per batch.</param>
    /// <returns>The batches.</returns>
    public static IEnumerable<IReadOnlyList<Guid>> Batch(IReadOnlyList<Guid> ids, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        for (var offset = 0; offset < ids.Count; offset += batchSize)
        {
            yield return ids.Skip(offset).Take(batchSize).ToList();
        }
    }

    /// <summary>
    /// Returns every artist the user can see, with the counts Jellyfin tracks for each.
    /// </summary>
    /// <param name="user">The user whose visibility applies.</param>
    /// <returns>Artists and their item counts.</returns>
    public IReadOnlyList<(BaseItem Item, ItemCounts Counts)> Artists(User user) =>
        _libraryManager.GetArtists(NamedQuery(user)).Items;

    /// <summary>
    /// Returns the identifiers of artists credited as album artists.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="Artists"/> because the snapshot carries every artist —
    /// including guests credited only on individual tracks, since tracks reference them — while
    /// browsing by artist should show only these.
    /// </remarks>
    /// <param name="user">The user whose visibility applies.</param>
    /// <returns>Album-artist identifiers.</returns>
    public IReadOnlySet<Guid> AlbumArtistIds(User user) =>
        _libraryManager.GetAlbumArtists(NamedQuery(user)).Items
            .Select(a => a.Item.Id)
            .ToHashSet();

    /// <summary>
    /// Returns every music genre the user can see.
    /// </summary>
    /// <param name="user">The user whose visibility applies.</param>
    /// <returns>Genres and their item counts.</returns>
    public IReadOnlyList<(BaseItem Item, ItemCounts Counts)> Genres(User user) =>
        _libraryManager.GetMusicGenres(NamedQuery(user)).Items;

    /// <summary>
    /// Returns the playlists the user can see.
    /// </summary>
    /// <remarks>
    /// Playlists carry their own visibility rules on top of library access — ownership, open
    /// access, and explicit shares — which <c>IsVisible</c> applies.
    /// </remarks>
    /// <param name="user">The user whose visibility applies.</param>
    /// <param name="audioOnly">Whether to keep only audio playlists.</param>
    /// <returns>Visible playlists, in protocol order.</returns>
    public IReadOnlyList<Playlist> Playlists(User user, bool audioOnly)
    {
        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            DtoOptions = new DtoOptions(true) { EnableImages = true, EnableUserData = true }
        };

        return _libraryManager.GetItemList(query)
            .OfType<Playlist>()
            .Where(p => p.IsVisible(user, false))
            .Where(p => !audioOnly || p.PlaylistMediaType == MediaType.Audio)
            .OrderBy(p => p.Id.ToString("N"), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Resolves a genre name to the identifier Jellyfin's own DTOs use.
    /// </summary>
    /// <remarks>
    /// A deterministic hash of the genre's path, not a database lookup, so calling it per track
    /// costs nothing.
    /// </remarks>
    /// <param name="name">Genre name.</param>
    /// <returns>The genre identifier.</returns>
    public Guid GenreId(string name) => _libraryManager.GetMusicGenreId(name);

    /// <summary>
    /// Builds the artist name to identifier map used to resolve track credits.
    /// </summary>
    /// <remarks>
    /// Built once for the whole library. Resolving credits per track through
    /// <c>GetArtist(name)</c> would be a database lookup for each of tens of thousands of tracks.
    /// Comparison is case-insensitive, matching how Jellyfin resolves artist names.
    /// </remarks>
    /// <param name="artists">Artists from <see cref="Artists"/>.</param>
    /// <returns>Name to identifier.</returns>
    public static IReadOnlyDictionary<string, Guid> ArtistIdsByName(
        IReadOnlyList<(BaseItem Item, ItemCounts Counts)> artists)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var (item, _) in artists)
        {
            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                // First writer wins: duplicates are a library-tagging artifact, and picking
                // deterministically keeps two builds byte-identical.
                map.TryAdd(item.Name, item.Id);
            }
        }

        return map;
    }

    /// <summary>
    /// The query shape used for artist and genre lookups, which are item-by-name queries rather
    /// than item queries.
    /// </summary>
    private static InternalItemsQuery NamedQuery(User user) =>
        new InternalItemsQuery(user)
        {
            Recursive = true,
            DtoOptions = new DtoOptions(true) { EnableImages = true, EnableUserData = true }
        };
}
