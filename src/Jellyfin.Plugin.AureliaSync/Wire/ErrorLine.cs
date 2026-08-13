using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire;

/// <summary>
/// An in-band failure line.
/// </summary>
/// <remarks>
/// Only ever written once the response body has already started. Before that a failure is an HTTP
/// status plus the standard error envelope; Jellyfin's exception middleware rethrows once the
/// response has begun, so the status can no longer be changed at that point.
/// </remarks>
public class ErrorLine
{
    /// <summary>Gets the line discriminator.</summary>
    [JsonPropertyName("kind")]
    public string Kind => WireKind.Error;

    /// <summary>Gets or sets the machine-readable error code.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Gets or sets human-readable detail, safe to display.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the identifier tying this line to the server log.</summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;
}
