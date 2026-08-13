using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// Detail of a failed request.
/// </summary>
public class SyncErrorDetail
{
    /// <summary>
    /// Gets or sets the machine-readable code the client branches on.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets human-readable detail. Never contains tokens, payloads, or another user's data.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the identifier tying this response to the server-side log entry.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether repeating the request may succeed later.
    /// </summary>
    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether recovery requires a fresh snapshot.
    /// </summary>
    /// <remarks>
    /// Even when true, the client must keep serving its existing local library until a replacement
    /// snapshot is fully committed. No error ever instructs a client to discard good local state.
    /// </remarks>
    [JsonPropertyName("requiresSnapshot")]
    public bool RequiresSnapshot { get; set; }
}
