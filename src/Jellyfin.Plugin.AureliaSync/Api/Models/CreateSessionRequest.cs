using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// Request to open or resume a delivery session.
/// </summary>
/// <remarks>
/// The version fields are flat rather than nested range objects, matching the client's encoder.
/// There is deliberately no user field: identity comes only from the authenticated principal.
/// </remarks>
public class CreateSessionRequest
{
    /// <summary>
    /// Gets or sets the durable per-installation identifier, distinct from the Jellyfin device id.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Gets or sets the client's version, for diagnostics.</summary>
    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }

    /// <summary>Gets or sets the lowest protocol version the client speaks.</summary>
    [JsonPropertyName("protocolMin")]
    public int ProtocolMin { get; set; }

    /// <summary>Gets or sets the highest protocol version the client speaks.</summary>
    [JsonPropertyName("protocolMax")]
    public int ProtocolMax { get; set; }

    /// <summary>Gets or sets the lowest wire schema the client understands.</summary>
    [JsonPropertyName("schemaMin")]
    public int SchemaMin { get; set; }

    /// <summary>Gets or sets the highest wire schema the client understands.</summary>
    [JsonPropertyName("schemaMax")]
    public int SchemaMax { get; set; }

    /// <summary>
    /// Gets or sets the client's durable position, if it has one.
    /// </summary>
    /// <remarks>
    /// This is the only resume state the client sends — never a cursor — so the token has to carry
    /// the position by itself.
    /// </remarks>
    [JsonPropertyName("checkpointToken")]
    public string? CheckpointToken { get; set; }

    /// <summary>Gets or sets a value indicating whether to discard any checkpoint and start over.</summary>
    [JsonPropertyName("reset")]
    public bool Reset { get; set; }
}
