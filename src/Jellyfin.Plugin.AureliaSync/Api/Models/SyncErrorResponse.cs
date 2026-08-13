using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// The error envelope returned for every non-success response.
/// </summary>
public class SyncErrorResponse
{
    /// <summary>
    /// Gets or sets the error detail.
    /// </summary>
    [JsonPropertyName("error")]
    public SyncErrorDetail Error { get; set; } = new SyncErrorDetail();

    /// <summary>
    /// Builds an error envelope.
    /// </summary>
    /// <param name="code">A <see cref="SyncErrorCode"/> value.</param>
    /// <param name="message">Human-readable detail, safe to show a user.</param>
    /// <param name="retryable">Whether repeating the same request may succeed later.</param>
    /// <param name="requiresSnapshot">Whether recovery requires a fresh snapshot.</param>
    /// <param name="correlationId">Correlation identifier; generated when omitted.</param>
    /// <returns>The envelope.</returns>
    public static SyncErrorResponse Create(
        string code,
        string message,
        bool retryable = false,
        bool requiresSnapshot = false,
        string? correlationId = null)
    {
        return new SyncErrorResponse
        {
            Error = new SyncErrorDetail
            {
                Code = code,
                Message = message,
                Retryable = retryable,
                RequiresSnapshot = requiresSnapshot,
                CorrelationId = correlationId ?? NewCorrelationId()
            }
        };
    }

    /// <summary>
    /// Creates a correlation identifier for pairing a client-visible error with server logs.
    /// </summary>
    /// <returns>A 32-character hexadecimal identifier.</returns>
    public static string NewCorrelationId() => Guid.NewGuid().ToString("N");
}
