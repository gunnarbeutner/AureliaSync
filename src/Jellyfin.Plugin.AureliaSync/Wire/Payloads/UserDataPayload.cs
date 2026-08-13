using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire.Payloads;

/// <summary>
/// Wire representation of one item's per-user state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Omitting a field never clears it.</b> The client applies these with SQL <c>COALESCE</c>, so
/// an absent or null value leaves whatever it already had. To un-favourite an item send
/// <c>false</c>; to clear a resume position send <c>0</c>. Sending null does nothing at all.
/// </para>
/// <para>
/// Scoped to the authenticated user. These records must never cross users.
/// </para>
/// </remarks>
public class UserDataPayload
{
    /// <summary>Gets or sets the identifier of the item this state belongs to.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the item is favourited. Send false to clear, never null.</summary>
    [JsonPropertyName("isFavorite")]
    public bool? IsFavorite { get; set; }

    /// <summary>Gets or sets the play count. Send 0 to clear, never null.</summary>
    [JsonPropertyName("playCount")]
    public int? PlayCount { get; set; }

    /// <summary>Gets or sets when the item was last played.</summary>
    [JsonPropertyName("lastPlayedAt")]
    public DateTimeOffset? LastPlayedAt { get; set; }

    /// <summary>
    /// Gets or sets the resume position in Jellyfin ticks (100 nanoseconds).
    /// </summary>
    /// <remarks>
    /// The only ticks-valued field in the protocol; track durations are seconds. Send 0 to clear,
    /// never null.
    /// </remarks>
    [JsonPropertyName("playbackPositionTicks")]
    public long? PlaybackPositionTicks { get; set; }
}
