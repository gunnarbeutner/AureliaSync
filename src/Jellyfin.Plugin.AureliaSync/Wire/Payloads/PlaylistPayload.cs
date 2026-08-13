using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire.Payloads;

/// <summary>
/// Wire representation of a playlist.
/// </summary>
public class PlaylistPayload
{
    /// <summary>Gets or sets the playlist identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the sort name.</summary>
    [JsonPropertyName("sortName")]
    public string? SortName { get; set; }

    /// <summary>
    /// Gets or sets the number of entries.
    /// </summary>
    /// <remarks>
    /// This must equal the number of <c>playlist.replace</c> records actually sent for the
    /// playlist — after duplicate removal — rather than Jellyfin's raw child count. The client
    /// cannot represent a repeated track, so a raw count would show a total its own list can never
    /// reach.
    /// </remarks>
    [JsonPropertyName("trackCount")]
    public int? TrackCount { get; set; }

    /// <summary>Gets or sets the Primary image cache tag.</summary>
    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets when the playlist was created.</summary>
    [JsonPropertyName("dateCreated")]
    public DateTimeOffset? DateCreated { get; set; }

    /// <summary>Gets or sets a value indicating whether the calling user favourited this playlist.</summary>
    [JsonPropertyName("isFavorite")]
    public bool? IsFavorite { get; set; }
}
