using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AureliaSync.Wire;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Projection;

/// <summary>
/// Reads Jellyfin entities into <see cref="ItemFacts"/>.
/// </summary>
/// <remarks>
/// Deliberately thin: it makes no decisions about the wire format, so that everything with a
/// judgement call in it lives in <see cref="PayloadProjector"/> where it can be unit tested.
/// Constructing a real <c>Audio</c> in a test would drag in <c>BaseItem</c>'s static
/// <c>LibraryManager</c> and <c>UserDataManager</c> dependencies, so this class is validated
/// against a real server instead.
/// </remarks>
public sealed class BaseItemFactsReader
{
    private readonly IImageProcessor _imageProcessor;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseItemFactsReader"/> class.
    /// </summary>
    /// <param name="imageProcessor">Used to compute image cache tags.</param>
    /// <param name="logger">Logger.</param>
    public BaseItemFactsReader(IImageProcessor imageProcessor, ILogger logger)
    {
        _imageProcessor = imageProcessor;
        _logger = logger;
    }

    /// <summary>
    /// Reads one item.
    /// </summary>
    /// <param name="item">The Jellyfin entity.</param>
    /// <param name="userId">The user whose state should be attached.</param>
    /// <param name="knownAlbumIds">
    /// Album identifiers already enumerated for this snapshot. When a track's parent is in this
    /// set it is used directly, which avoids <c>AlbumEntity</c> walking the parent chain — a
    /// per-track cost that would be paid 30,000 times.
    /// </param>
    /// <returns>The facts needed to project this item.</returns>
    public ItemFacts Read(BaseItem item, Guid userId, IReadOnlySet<Guid>? knownAlbumIds = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var artistNames = Array.Empty<string>() as IReadOnlyList<string>;
        var albumArtistNames = Array.Empty<string>() as IReadOnlyList<string>;
        Guid? albumId = null;

        switch (item)
        {
            case Audio audio:
                artistNames = audio.Artists;
                albumArtistNames = audio.AlbumArtists;
                albumId = ResolveAlbumId(audio, knownAlbumIds);
                break;

            case MusicAlbum album:
                artistNames = album.Artists;
                albumArtistNames = album.AlbumArtists;
                break;
        }

        return new ItemFacts
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            SortName = item.SortName,
            Overview = item.Overview,
            ProductionYear = item.ProductionYear,
            DateCreated = ToOffset(item.DateCreated),
            RunTimeTicks = item.RunTimeTicks,
            IndexNumber = item.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber,
            AlbumId = albumId,
            ArtistNames = artistNames,
            AlbumArtistNames = albumArtistNames,
            GenreNames = item.Genres ?? Array.Empty<string>(),
            ImageTag = ReadPrimaryImageTag(item),
            UserData = ReadUserData(item, userId)
        };
    }

    /// <summary>
    /// Computes the Primary image cache tag, which the client turns into a URL.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The tag, or null when the item has no usable primary image.</returns>
    public string? ReadPrimaryImageTag(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var image = item.GetImageInfo(ImageType.Primary, 0);
        if (image is null)
        {
            return null;
        }

        try
        {
            return _imageProcessor.GetImageCacheTag(item, image);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            // A missing or unreadable image file must not fail a whole snapshot; the client simply
            // renders a placeholder for this one item.
            _logger.LogDebug(ex, "AureliaSync: no image tag for {ItemId}", item.Id);
            return null;
        }
    }

    /// <summary>
    /// Selects this user's row from the user data eagerly loaded alongside the item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <c>IUserDataManager.GetUserData</c>: that issues a database query per cache
    /// miss and pollutes the server's shared user-data cache, which over 34,500 items would hammer
    /// a live server. Hydrating with <c>DtoOptions.EnableUserData</c> brings the rows along in the
    /// same batched query instead.
    /// </para>
    /// <para>
    /// The selection mirrors Jellyfin's own <c>GetUserDataInternal</c>: an item can have several
    /// rows keyed by different data keys — that is how a remake shares watch state with its
    /// original — so prefer the row keyed by the item's own identifier and fall back to any other
    /// row the item's keys claim.
    /// </para>
    /// </remarks>
    /// <param name="item">The item, hydrated with user data.</param>
    /// <param name="userId">The user whose row is wanted.</param>
    /// <returns>The user's state, or null when they have none.</returns>
    public static UserDataFacts? ReadUserData(BaseItem item, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.UserData is not { Count: > 0 } rows)
        {
            return null;
        }

        var keys = item.GetUserDataKeys();
        var mine = rows.Where(u => u.UserId.Equals(userId) && keys.Contains(u.CustomDataKey)).ToList();
        if (mine.Count == 0)
        {
            return null;
        }

        var preferred = item.Id.ToString("N");
        var row = mine.FirstOrDefault(u => string.Equals(u.CustomDataKey, preferred, StringComparison.Ordinal))
            ?? mine[0];

        return new UserDataFacts
        {
            IsFavorite = row.IsFavorite,
            PlayCount = row.PlayCount,
            LastPlayedAt = ToOffset(row.LastPlayedDate),
            PlaybackPositionTicks = row.PlaybackPositionTicks,
            Played = row.Played
        };
    }

    private static Guid? ResolveAlbumId(Audio audio, IReadOnlySet<Guid>? knownAlbumIds)
    {
        // The cheap path: the track's parent is an album we already enumerated.
        if (!audio.ParentId.Equals(Guid.Empty) && knownAlbumIds?.Contains(audio.ParentId) == true)
        {
            return audio.ParentId;
        }

        // The fallback walks the parent chain, so it is reserved for tracks filed somewhere other
        // than directly under their album.
        var album = audio.AlbumEntity;
        return album is null ? null : album.Id;
    }

    private static DateTimeOffset? ToOffset(DateTime? value)
    {
        if (value is not { } dt || dt == DateTime.MinValue || dt == default)
        {
            return null;
        }

        return dt.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(dt, TimeSpan.Zero),
            DateTimeKind.Local => new DateTimeOffset(dt).ToUniversalTime(),

            // Jellyfin stores timestamps as UTC but often hands them back Unspecified; treating
            // them as local would shift every date by the server's offset.
            _ => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero)
        };
    }
}
