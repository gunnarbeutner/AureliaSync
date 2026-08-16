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
