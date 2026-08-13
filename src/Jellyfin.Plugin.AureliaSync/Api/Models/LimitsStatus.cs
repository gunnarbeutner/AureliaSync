using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// Delivery limits a client should plan around. Advisory: the server enforces its own bounds and
/// reports why a segment ended in <c>segment.end.stopReason</c>.
/// </summary>
public class LimitsStatus
{
    /// <summary>Gets or sets the maximum records the server will place in one segment.</summary>
    [JsonPropertyName("maxRecordsPerSegment")]
    public int MaxRecordsPerSegment { get; set; }

    /// <summary>Gets or sets the maximum uncompressed payload bytes in one segment.</summary>
    [JsonPropertyName("maxBytesPerSegment")]
    public long MaxBytesPerSegment { get; set; }

    /// <summary>Gets or sets the server's wall-clock budget for producing one segment.</summary>
    [JsonPropertyName("segmentTimeBudgetMs")]
    public int SegmentTimeBudgetMs { get; set; }
}
