using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire.Payloads;

/// <summary>
/// Wire representation of a track.
/// </summary>
public class TrackPayload
{
    /// <summary>Gets or sets the track identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the sort name.</summary>
    [JsonPropertyName("sortName")]
    public string? SortName { get; set; }

    /// <summary>Gets or sets the joined artist display string.</summary>
    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    /// <summary>Gets or sets the first artist credit's identifier. Note the lowercase 'd'.</summary>
    [JsonPropertyName("artistId")]
    public string? ArtistId { get; set; }

    /// <summary>
    /// Gets or sets every artist credit, in order.
    /// </summary>
    /// <remarks>
    /// Order is significant: the client stores each element's array index as the credit position
    /// in its <c>itemArtist</c> table. Reordering this list reorders the credits it displays.
    /// </remarks>
    [JsonPropertyName("artistIDs")]
    public IReadOnlyList<string>? ArtistIDs { get; set; }

    /// <summary>Gets or sets the album identifier. Note the lowercase 'd'.</summary>
    [JsonPropertyName("albumId")]
    public string? AlbumId { get; set; }

    /// <summary>
    /// Gets or sets the duration in <b>seconds</b>.
    /// </summary>
    /// <remarks>
    /// Deliberately not Jellyfin ticks. The client maps this straight onto a
    /// <c>TimeInterval</c>. The only ticks-valued field in the protocol is
    /// <see cref="UserDataPayload.PlaybackPositionTicks"/>.
    /// </remarks>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>Gets or sets the track number within its disc.</summary>
    [JsonPropertyName("indexNumber")]
    public int? IndexNumber { get; set; }

    /// <summary>Gets or sets the disc number.</summary>
    [JsonPropertyName("parentIndexNumber")]
    public int? ParentIndexNumber { get; set; }

    /// <summary>Gets or sets the release year.</summary>
    [JsonPropertyName("productionYear")]
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the genre identifiers. Note the uppercase 'IDs'.</summary>
    [JsonPropertyName("genreIDs")]
    public IReadOnlyList<string>? GenreIDs { get; set; }

    /// <summary>Gets or sets the track's own Primary image cache tag.</summary>
    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets a value indicating whether the calling user favourited this track.</summary>
    [JsonPropertyName("isFavorite")]
    public bool? IsFavorite { get; set; }
}
