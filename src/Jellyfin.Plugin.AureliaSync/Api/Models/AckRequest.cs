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

    /// <summary>
    /// Gets or sets a field kept only so an older client's acknowledgement still deserialises.
    /// </summary>
    /// <remarks>
    /// The server stopped emitting and stopped reading segment digests. Accepting the property and
    /// ignoring it means a client that still echoes one is not rejected for it.
    /// </remarks>
    [JsonPropertyName("aggregateChecksum")]
    [System.Obsolete("Segment digests were removed from the protocol; this is tolerated, never read.")]
    public string? AggregateChecksum { get; set; }
}
