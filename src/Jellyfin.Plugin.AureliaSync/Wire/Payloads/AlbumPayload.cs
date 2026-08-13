using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire.Payloads;

/// <summary>
/// Wire representation of an album.
/// </summary>
public class AlbumPayload
{
    /// <summary>Gets or sets the album identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the sort name.</summary>
    [JsonPropertyName("sortName")]
    public string? SortName { get; set; }

    /// <summary>Gets or sets the album artist's display name.</summary>
    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    /// <summary>Gets or sets the primary album artist's identifier. Note the lowercase 'd'.</summary>
    [JsonPropertyName("artistId")]
    public string? ArtistId { get; set; }

    /// <summary>Gets or sets the release year.</summary>
    [JsonPropertyName("productionYear")]
    public int? ProductionYear { get; set; }

    /// <summary>
    /// Gets or sets the number of tracks. This must equal the number of visible tracks actually
    /// sent for the album, not Jellyfin's raw child count, or the client's header disagrees with
    /// its own list.
    /// </summary>
    [JsonPropertyName("trackCount")]
    public int? TrackCount { get; set; }

    /// <summary>Gets or sets the genre identifiers. Note the uppercase 'IDs'.</summary>
    [JsonPropertyName("genreIDs")]
    public IReadOnlyList<string>? GenreIDs { get; set; }

    /// <summary>Gets or sets the Primary image cache tag.</summary>
    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets a value indicating whether the calling user favourited this album.</summary>
    [JsonPropertyName("isFavorite")]
    public bool? IsFavorite { get; set; }
}
