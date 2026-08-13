using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire.Payloads;

/// <summary>
/// One chunk of the set of identifiers a repaired catalog should contain.
/// </summary>
/// <remarks>
/// <para>
/// This exists because deletions are invisible to a timestamp scan. A repair can find everything
/// that <i>changed</i> since a client fell behind by asking Jellyfin for items saved since then, but
/// a deleted row has no timestamp — it is simply gone, and no forward scan will ever mention it.
/// Listing what remains is the cheap way to say what does not: about a megabyte of identifiers
/// against sixteen megabytes for a full catalog.
/// </para>
/// <para>
/// The chunks for one entity type are only meaningful <b>together</b>. A client must accumulate them
/// across the whole session and prune at promotion, never per record — pruning against a partial
/// manifest would delete most of the library.
/// </para>
/// </remarks>
public class ManifestPayload
{
    /// <summary>
    /// Gets or sets this chunk's identifier.
    /// </summary>
    /// <remarks>
    /// Unique within the session, and carried only because every record on the wire has an
    /// <c>id</c>. It addresses the chunk, not an entity, so it must not be stored.
    /// </remarks>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets which entity type this chunk enumerates.</summary>
    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>Gets or sets the identifiers that still exist.</summary>
    [JsonPropertyName("ids")]
    public IReadOnlyList<string> Ids { get; set; } = new List<string>();
}
