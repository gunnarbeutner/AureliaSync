using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// Response to an acknowledgement.
/// </summary>
public class AckResponse
{
    /// <summary>
    /// Gets or sets the client's new durable position.
    /// </summary>
    /// <remarks>
    /// The client stores this and sends it back when opening its next session; it is the only
    /// resume state the protocol carries.
    /// </remarks>
    [JsonPropertyName("checkpointToken")]
    public string? CheckpointToken { get; set; }

    /// <summary>Gets or sets the acknowledged position, for diagnostics.</summary>
    [JsonPropertyName("ackedCursor")]
    public string? AckedCursor { get; set; }

    /// <summary>Gets or sets a value indicating whether the whole snapshot is now acknowledged.</summary>
    [JsonPropertyName("snapshotComplete")]
    public bool SnapshotComplete { get; set; }
}
