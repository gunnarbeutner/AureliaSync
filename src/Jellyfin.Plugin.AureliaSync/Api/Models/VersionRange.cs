using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// An inclusive supported version range. Negotiation intersects the client's range with this one.
/// </summary>
public class VersionRange
{
    /// <summary>Gets or sets the lowest supported version.</summary>
    [JsonPropertyName("min")]
    public int Min { get; set; }

    /// <summary>Gets or sets the highest supported version.</summary>
    [JsonPropertyName("max")]
    public int Max { get; set; }
}
