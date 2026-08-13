using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// A cumulative acknowledgement.
/// </summary>
public class AckRequest
{
    /// <summary>
    /// Gets or sets the cursor through which everything has been durably committed locally.
    /// </summary>
    [JsonPropertyName("throughCursor")]
    public string ThroughCursor { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client's idempotency key for this acknowledgement.
    /// </summary>
    /// <remarks>
    /// Fresh per segment and reused verbatim on retry. Together with the user and client it forms
    /// the receipt key, which is what makes a replay after a crash safe.
    /// </remarks>
    [JsonPropertyName("clientCommitId")]
    public string ClientCommitId { get; set; } = string.Empty;

    /// <summary>Gets or sets how many records the client applied, excluding control lines.</summary>
    [JsonPropertyName("recordCount")]
    public int RecordCount { get; set; }

    /// <summary>Gets or sets the digest the server reported for the segment, echoed back.</summary>
    [JsonPropertyName("aggregateChecksum")]
    public string? AggregateChecksum { get; set; }
}
