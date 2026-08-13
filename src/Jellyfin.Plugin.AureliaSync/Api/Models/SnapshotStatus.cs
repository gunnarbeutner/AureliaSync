using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// The caller's most recent snapshot.
/// </summary>
public class SnapshotStatus
{
    /// <summary>
    /// Gets or sets the state: <c>none</c>, <c>building</c>, <c>complete</c>, <c>failed</c>, or
    /// <c>invalidated</c>. Only <c>complete</c> may be streamed.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "none";

    /// <summary>Gets or sets the snapshot generation, when one exists.</summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    /// <summary>Gets or sets the number of materialised rows, when known.</summary>
    [JsonPropertyName("rowCount")]
    public long? RowCount { get; set; }

    /// <summary>Gets or sets the phase currently being materialised, while building.</summary>
    [JsonPropertyName("phase")]
    public string? Phase { get; set; }
}
