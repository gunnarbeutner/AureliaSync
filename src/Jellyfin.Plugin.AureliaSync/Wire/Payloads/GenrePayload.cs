using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire.Payloads;

/// <summary>
/// Wire representation of a genre.
/// </summary>
/// <remarks>
/// Genres deliberately carry no artwork, sort name, or user state: the client's genre model has
/// nowhere to put them.
/// </remarks>
public class GenrePayload
{
    /// <summary>
    /// Gets or sets the genre identifier.
    /// </summary>
    /// <remarks>
    /// Derived from <c>ILibraryManager.GetMusicGenreId(name)</c>, a deterministic hash of the
    /// genre's path, so it matches the identifier Jellyfin's own DTOs report without a database
    /// lookup.
    /// </remarks>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the genre name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
