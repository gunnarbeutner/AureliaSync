using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// Journal extents. A client whose checkpoint is below <see cref="Floor"/> has a gap and needs a
/// fresh snapshot rather than a change session.
/// </summary>
public class JournalStatus
{
    /// <summary>Gets or sets the highest journal sequence recorded.</summary>
    [JsonPropertyName("head")]
    public long Head { get; set; }

    /// <summary>Gets or sets the lowest journal sequence still retained.</summary>
    [JsonPropertyName("floor")]
    public long Floor { get; set; }

    /// <summary>Gets or sets the number of retained journal records.</summary>
    [JsonPropertyName("records")]
    public long Records { get; set; }
}
