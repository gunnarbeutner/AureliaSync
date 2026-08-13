using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire.Payloads;

/// <summary>
/// Wire representation of an artist.
/// </summary>
/// <remarks>
/// Every property is pinned with <see cref="JsonPropertyNameAttribute"/>. The client decodes with
/// <c>.useDefaultKeys</c>, so a key that differs by so much as a capital letter simply fails to
/// decode into its field.
/// </remarks>
public class ArtistPayload
{
    /// <summary>Gets or sets the artist identifier, 32-character lowercase hexadecimal.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the sort name.</summary>
    [JsonPropertyName("sortName")]
    public string? SortName { get; set; }

    /// <summary>Gets or sets the biography (Jellyfin's overview).</summary>
    [JsonPropertyName("biography")]
    public string? Biography { get; set; }

    /// <summary>Gets or sets the number of albums by this artist.</summary>
    [JsonPropertyName("albumCount")]
    public int? AlbumCount { get; set; }

    /// <summary>Gets or sets the Primary image cache tag. The client composes the URL.</summary>
    [JsonPropertyName("imageTag")]
    public string? ImageTag { get; set; }

    /// <summary>Gets or sets a value indicating whether the calling user favourited this artist.</summary>
    [JsonPropertyName("isFavorite")]
    public bool? IsFavorite { get; set; }
}
