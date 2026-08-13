using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire;

/// <summary>
/// The first line of every segment.
/// </summary>
/// <remarks>
/// Carrying the schema and protocol versions here rather than on every record saves roughly
/// 22 bytes per record, which over a 34,500-record snapshot is most of a megabyte.
/// </remarks>
public class SegmentBegin
{
    /// <summary>Gets the line discriminator.</summary>
    [JsonPropertyName("kind")]
    public string Kind => WireKind.SegmentBegin;

    /// <summary>Gets or sets the wire schema version of every payload in this segment.</summary>
    [JsonPropertyName("wireSchemaVersion")]
    public int WireSchemaVersion { get; set; } = WireSchema.WireSchemaVersionMax;

    /// <summary>Gets or sets the negotiated protocol version.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = WireSchema.ProtocolVersionMax;

    /// <summary>Gets or sets the owning session.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the delivery mode: <c>snapshot</c> or <c>changes</c>.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "snapshot";

    /// <summary>Gets or sets the snapshot generation these records belong to.</summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; set; }

    /// <summary>Gets or sets the cursor this segment continues from, echoed back for diagnostics.</summary>
    [JsonPropertyName("afterCursor")]
    public string? AfterCursor { get; set; }

    /// <summary>Gets or sets the server's clock, so clients need not trust their own.</summary>
    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; set; }
}
