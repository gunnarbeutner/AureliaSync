using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AureliaSync.Projection;

/// <summary>
/// Everything the wire format needs about one library item, with no Jellyfin types attached.
/// </summary>
/// <remarks>
/// This is the seam that makes projection testable. Constructing a real <c>Audio</c> or
/// <c>MusicAlbum</c> in a test drags in <c>BaseItem</c>'s static <c>LibraryManager</c> and
/// <c>UserDataManager</c> dependencies; a plain record does not. Reading Jellyfin entities into
/// this shape is <c>BaseItemFactsReader</c>'s job and is deliberately kept trivial, so that
/// everything with actual decisions in it lives in <see cref="PayloadProjector"/> and is covered by
/// ordinary unit tests.
/// </remarks>
public sealed record ItemFacts
{
    /// <summary>Gets the item identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the sort name, when it differs meaningfully from the name.</summary>
    public string? SortName { get; init; }

    /// <summary>Gets the overview, used as an artist biography.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the release year.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets when the item was added to the library.</summary>
    public DateTimeOffset? DateCreated { get; init; }

    /// <summary>Gets the runtime in Jellyfin ticks, converted to seconds on the wire.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets the track number.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>Gets the disc number.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>Gets the owning album's identifier.</summary>
    public Guid? AlbumId { get; init; }

    /// <summary>Gets the artist credits, in order. Order is preserved onto the wire.</summary>
    public IReadOnlyList<string> ArtistNames { get; init; } = Array.Empty<string>();

    /// <summary>Gets the album-artist credits, in order.</summary>
    public IReadOnlyList<string> AlbumArtistNames { get; init; } = Array.Empty<string>();

    /// <summary>Gets the genre names, resolved to identifiers during projection.</summary>
    public IReadOnlyList<string> GenreNames { get; init; } = Array.Empty<string>();

    /// <summary>Gets the Primary image cache tag.</summary>
    public string? ImageTag { get; init; }

    /// <summary>Gets the calling user's state for this item, when they have any.</summary>
    public UserDataFacts? UserData { get; init; }
}
