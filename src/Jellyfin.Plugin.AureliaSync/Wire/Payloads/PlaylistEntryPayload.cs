using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire.Payloads;

/// <summary>
/// Wire representation of a single playlist entry.
/// </summary>
/// <remarks>
/// <para>
/// A <c>playlist.replace</c> record describes <b>one entry</b>, not a whole playlist, and carries
/// the complete track payload for that entry — the client inserts the membership row and upserts
/// the track from the same record.
/// </para>
/// <para>
/// All entries belonging to one playlist must be delivered in a single segment: the client deletes
/// that playlist's membership and reinserts only the rows present in the segment it is applying.
/// </para>
/// </remarks>
public class PlaylistEntryPayload : TrackPayload
{
    /// <summary>Gets or sets the owning playlist's identifier. Note the uppercase 'ID'.</summary>
    [JsonPropertyName("playlistID")]
    public string PlaylistID { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets Jellyfin's identifier for this specific membership, which is distinct from the
    /// track identifier.
    /// </summary>
    [JsonPropertyName("playlistEntryID")]
    public string? PlaylistEntryID { get; set; }

    /// <summary>
    /// Gets or sets the zero-based position within the playlist.
    /// </summary>
    /// <remarks>
    /// Positions are dense. Because the client's membership table is keyed on
    /// (playlist, item), a track repeated within a playlist cannot be represented: repeats are
    /// dropped, keeping the first occurrence, and the remaining entries renumbered.
    /// </remarks>
    [JsonPropertyName("position")]
    public int Position { get; set; }
}
