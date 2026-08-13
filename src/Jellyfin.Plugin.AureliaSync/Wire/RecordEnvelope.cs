using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire;

/// <summary>
/// The envelope wrapping one record on the wire.
/// </summary>
/// <remarks>
/// <para>
/// The streaming path does not serialise this type. Payloads are stored as wire-ready UTF-8 bytes
/// and copied straight through with <c>Utf8JsonWriter.WriteRawValue</c>, which turns streaming into
/// a memory copy rather than a deserialise/reserialise round trip for every one of tens of
/// thousands of records.
/// </para>
/// <para>
/// This type exists so the envelope's shape is stated once, in C#, and so fixtures and tests
/// generate exactly the shape the writer emits.
/// </para>
/// </remarks>
public class RecordEnvelope
{
    /// <summary>
    /// Gets or sets the position of this record.
    /// </summary>
    /// <remarks>
    /// Required on <b>every</b> record line, including kinds the server treats as metadata: the
    /// client's decoder fails hard on a record without one.
    /// </remarks>
    [JsonPropertyName("cursor")]
    public string Cursor { get; set; } = string.Empty;

    /// <summary>Gets or sets the monotonic sequence number, for diagnostics.</summary>
    [JsonPropertyName("sequence")]
    public long? Sequence { get; set; }

    /// <summary>Gets or sets the record kind. See <see cref="WireKind"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the entity type for an item upsert. See <see cref="WireEntityType"/>.</summary>
    [JsonPropertyName("entityType")]
    public string? EntityType { get; set; }

    /// <summary>Gets or sets the entity identifier, which the client uses as a fallback.</summary>
    [JsonPropertyName("entityId")]
    public string? EntityId { get; set; }

    /// <summary>Gets or sets the payload.</summary>
    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}
