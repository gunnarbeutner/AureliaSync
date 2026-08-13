using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Api.Models;

/// <summary>
/// The calling user's subscription state. Never describes any other user.
/// </summary>
public class UserStatus
{
    /// <summary>
    /// Gets or sets the authenticated user's identifier, in 32-character lowercase hexadecimal form.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether any client of this user holds a checkpoint.</summary>
    [JsonPropertyName("hasCheckpoint")]
    public bool HasCheckpoint { get; set; }

    /// <summary>Gets or sets a value indicating whether the next session will require a fresh snapshot.</summary>
    [JsonPropertyName("needsSnapshot")]
    public bool NeedsSnapshot { get; set; } = true;
}
