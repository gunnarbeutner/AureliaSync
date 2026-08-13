using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AureliaSync.Wire.Payloads;

namespace Jellyfin.Plugin.AureliaSync.Projection;

/// <summary>
/// Turns <see cref="ItemFacts"/> into wire payloads.
/// </summary>
/// <remarks>
/// Pure and fully unit tested: every decision about what the client actually receives is made here
/// rather than in the code that talks to Jellyfin.
/// </remarks>
public sealed class PayloadProjector
{
    /// <summary>
    /// Separator between artist credits in the flattened display string.
    /// </summary>
    public const string ArtistSeparator = ", ";

    private readonly IReadOnlyDictionary<string, Guid> _artistIdsByName;
    private readonly IReadOnlyDictionary<Guid, AlbumSummary> _albums;
    private readonly Func<string, Guid> _genreIdResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadProjector"/> class.
    /// </summary>
    /// <param name="artistIdsByName">
    /// Artist name to identifier, built once for the whole library. Comparison must be
    /// case-insensitive, matching how Jellyfin resolves artist names.
    /// </param>
    /// <param name="albums">Album identifier to summary, filled while projecting albums.</param>
    /// <param name="genreIdResolver">
    /// Resolves a genre name to its identifier. Backed by <c>GetMusicGenreId</c>, which is a
    /// deterministic hash rather than a database lookup.
    /// </param>
    public PayloadProjector(
        IReadOnlyDictionary<string, Guid> artistIdsByName,
        IReadOnlyDictionary<Guid, AlbumSummary> albums,
        Func<string, Guid> genreIdResolver)
    {
        _artistIdsByName = artistIdsByName;
        _albums = albums;
        _genreIdResolver = genreIdResolver;
    }

    /// <summary>
    /// Formats an identifier the way Jellyfin and the client both expect it.
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <returns>32 lowercase hexadecimal characters, no dashes.</returns>
    public static string FormatId(Guid id) => id.ToString("N");

    /// <summary>
    /// Converts Jellyfin ticks to the seconds the protocol carries.
    /// </summary>
    /// <remarks>
    /// Rounded to milliseconds: full double precision would add several bytes to every one of
    /// 30,000 track records to express a precision no player uses.
    /// </remarks>
    /// <param name="ticks">Runtime in ticks.</param>
    /// <returns>Seconds, or null when unknown.</returns>
    public static double? TicksToSeconds(long? ticks) =>
        ticks is null or <= 0 ? null : Math.Round(ticks.Value / (double)TimeSpan.TicksPerSecond, 3);

    /// <summary>Projects a track.</summary>
    /// <param name="facts">Source facts.</param>
    /// <returns>The wire payload.</returns>
    public TrackPayload Track(ItemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var artistIds = ResolveArtists(facts.ArtistNames);
        var album = facts.AlbumId is { } albumId && _albums.TryGetValue(albumId, out var summary)
            ? summary
            : default;

        return new TrackPayload
        {
            Id = FormatId(facts.Id),
            Name = facts.Name,
            SortName = NullIfSameAsName(facts.SortName, facts.Name),
            ArtistName = JoinArtists(facts.ArtistNames),
            ArtistId = artistIds.Count > 0 ? artistIds[0] : null,
            ArtistIDs = artistIds.Count > 0 ? artistIds : null,
            AlbumName = album.Name,
            AlbumId = facts.AlbumId is { } id ? FormatId(id) : null,
            Duration = TicksToSeconds(facts.RunTimeTicks),
            IndexNumber = facts.IndexNumber,
            ParentIndexNumber = facts.ParentIndexNumber,
            ProductionYear = facts.ProductionYear,
            GenreIDs = ResolveGenres(facts.GenreNames),
            ImageTag = facts.ImageTag,
            AlbumImageTag = album.ImageTag,
            IsFavorite = facts.UserData?.IsFavorite
        };
    }

    /// <summary>Projects an album.</summary>
    /// <param name="facts">Source facts.</param>
    /// <param name="trackCount">
    /// Number of visible tracks actually being sent for this album, which is not necessarily
    /// Jellyfin's child count.
    /// </param>
    /// <returns>The wire payload.</returns>
    public AlbumPayload Album(ItemFacts facts, int? trackCount)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Prefer the album-artist credit; fall back to the performing artists so an album whose
        // album-artist tag is missing still shows something rather than "Unknown Artist".
        var credits = facts.AlbumArtistNames.Count > 0 ? facts.AlbumArtistNames : facts.ArtistNames;
        var artistIds = ResolveArtists(credits);

        return new AlbumPayload
        {
            Id = FormatId(facts.Id),
            Name = facts.Name,
            SortName = NullIfSameAsName(facts.SortName, facts.Name),
            ArtistName = JoinArtists(credits),
            ArtistId = artistIds.Count > 0 ? artistIds[0] : null,
            ProductionYear = facts.ProductionYear,
            TrackCount = trackCount,
            GenreIDs = ResolveGenres(facts.GenreNames),
            ImageTag = facts.ImageTag,
            IsFavorite = facts.UserData?.IsFavorite
        };
    }

    /// <summary>Projects an artist.</summary>
    /// <param name="facts">Source facts.</param>
    /// <param name="albumCount">Number of albums credited to this artist.</param>
    /// <param name="isAlbumArtist">
    /// Whether Jellyfin credits this artist as an album artist rather than only on individual
    /// tracks. The client cannot derive this from anything else in the stream.
    /// </param>
    /// <returns>The wire payload.</returns>
    public ArtistPayload Artist(ItemFacts facts, int? albumCount, bool isAlbumArtist)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return new ArtistPayload
        {
            Id = FormatId(facts.Id),
            Name = facts.Name,
            SortName = NullIfSameAsName(facts.SortName, facts.Name),
            Biography = facts.Overview,
            AlbumCount = albumCount,
            ImageTag = facts.ImageTag,
            IsAlbumArtist = isAlbumArtist,
            IsFavorite = facts.UserData?.IsFavorite
        };
    }

    /// <summary>Projects a playlist.</summary>
    /// <param name="facts">Source facts.</param>
    /// <param name="entryCount">
    /// Number of entries actually sent, after duplicates are removed. Sending Jellyfin's raw child
    /// count would show a total the client's own list can never reach.
    /// </param>
    /// <returns>The wire payload.</returns>
    public PlaylistPayload Playlist(ItemFacts facts, int? entryCount)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return new PlaylistPayload
        {
            Id = FormatId(facts.Id),
            Name = facts.Name,
            SortName = NullIfSameAsName(facts.SortName, facts.Name),
            TrackCount = entryCount,
            ImageTag = facts.ImageTag,
            DateCreated = facts.DateCreated,
            IsFavorite = facts.UserData?.IsFavorite
        };
    }

    /// <summary>Projects a genre.</summary>
    /// <param name="id">Genre identifier.</param>
    /// <param name="name">Genre name.</param>
    /// <param name="albumCount">Number of albums in the genre.</param>
    /// <returns>The wire payload.</returns>
    public GenrePayload Genre(Guid id, string name, int? albumCount) =>
        new GenrePayload
        {
            Id = FormatId(id),
            Name = name,
            AlbumCount = albumCount
        };

    /// <summary>
    /// Projects one playlist entry, carrying the whole track with it.
    /// </summary>
    /// <param name="track">Facts for the track at this position.</param>
    /// <param name="playlistId">Owning playlist.</param>
    /// <param name="entryId">Jellyfin's identifier for this membership.</param>
    /// <param name="position">Zero-based, dense position.</param>
    /// <returns>The wire payload.</returns>
    public PlaylistEntryPayload PlaylistEntry(ItemFacts track, Guid playlistId, string? entryId, int position)
    {
        var projected = Track(track);

        return new PlaylistEntryPayload
        {
            Id = projected.Id,
            Name = projected.Name,
            SortName = projected.SortName,
            ArtistName = projected.ArtistName,
            ArtistId = projected.ArtistId,
            ArtistIDs = projected.ArtistIDs,
            AlbumName = projected.AlbumName,
            AlbumId = projected.AlbumId,
            Duration = projected.Duration,
            IndexNumber = projected.IndexNumber,
            ParentIndexNumber = projected.ParentIndexNumber,
            ProductionYear = projected.ProductionYear,
            GenreIDs = projected.GenreIDs,
            ImageTag = projected.ImageTag,
            AlbumImageTag = projected.AlbumImageTag,
            IsFavorite = projected.IsFavorite,
            PlaylistID = FormatId(playlistId),
            PlaylistEntryID = entryId,
            Position = position
        };
    }

    /// <summary>
    /// Projects per-user state, or null when there is nothing worth sending.
    /// </summary>
    /// <param name="facts">Source facts.</param>
    /// <returns>The wire payload, or null.</returns>
    public UserDataPayload? UserData(ItemFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.UserData is not { IsWorthSending: true } data)
        {
            return null;
        }

        return new UserDataPayload
        {
            Id = FormatId(facts.Id),
            IsFavorite = data.IsFavorite,
            PlayCount = data.PlayCount,
            LastPlayedAt = data.LastPlayedAt,
            PlaybackPositionTicks = data.PlaybackPositionTicks
        };
    }

    private static string? JoinArtists(IReadOnlyList<string> names)
    {
        var usable = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return usable.Count == 0 ? null : string.Join(ArtistSeparator, usable);
    }

    /// <summary>
    /// Drops a sort name that adds nothing, since it is stored per item on the client.
    /// </summary>
    private static string? NullIfSameAsName(string? sortName, string name) =>
        string.IsNullOrWhiteSpace(sortName) || string.Equals(sortName, name, StringComparison.Ordinal)
            ? null
            : sortName;

    /// <summary>
    /// Resolves credits to identifiers, preserving order and dropping names Jellyfin has no artist
    /// entity for.
    /// </summary>
    /// <param name="names">Credit names in order.</param>
    /// <returns>Resolved identifiers.</returns>
    /// <remarks>
    /// Order is load-bearing: the client stores each element's array index as the credit position.
    /// Duplicates are removed because the client's relation table is keyed on (item, artist), so a
    /// repeated credit would collapse anyway and shift every later position.
    /// </remarks>
    public IReadOnlyList<string> ResolveArtists(IReadOnlyList<string> names)
    {
        var resolved = new List<string>(names.Count);
        var seen = new HashSet<Guid>();

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)
                || !_artistIdsByName.TryGetValue(name, out var id)
                || !seen.Add(id))
            {
                continue;
            }

            resolved.Add(FormatId(id));
        }

        return resolved;
    }

    /// <summary>
    /// Resolves genre names to identifiers, deduplicated and in order.
    /// </summary>
    /// <param name="names">Genre names.</param>
    /// <returns>Resolved identifiers, or null when there are none.</returns>
    public IReadOnlyList<string>? ResolveGenres(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return null;
        }

        var resolved = new List<string>(names.Count);
        var seen = new HashSet<Guid>();

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var id = _genreIdResolver(name);
            if (id.Equals(Guid.Empty) || !seen.Add(id))
            {
                continue;
            }

            resolved.Add(FormatId(id));
        }

        return resolved.Count == 0 ? null : resolved;
    }
}
