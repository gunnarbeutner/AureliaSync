using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire;

/// <summary>
/// Protocol and wire-schema version constants, and the serializer settings the wire format depends on.
/// </summary>
public static class WireSchema
{
    /// <summary>
    /// Oldest protocol version this build can speak.
    /// </summary>
    public const int ProtocolVersionMin = 1;

    /// <summary>
    /// Newest protocol version this build can speak.
    /// </summary>
    public const int ProtocolVersionMax = 1;

    /// <summary>
    /// Oldest wire-schema version this build can emit.
    /// </summary>
    public const int WireSchemaVersionMin = 1;

    /// <summary>
    /// Newest wire-schema version this build can emit.
    /// </summary>
    public const int WireSchemaVersionMax = 1;

    /// <summary>
    /// The Jellyfin ABI this build targets, matching the release manifest.
    /// </summary>
    public const string TargetAbi = "10.11.0.0";

    /// <summary>
    /// Content type of a streamed segment.
    /// </summary>
    public const string NdjsonContentType = "application/x-ndjson";

    /// <summary>
    /// Serializer options for everything this plugin emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jellyfin configures MVC with <c>PropertyNamingPolicy = null</c>, so plugin controllers
    /// serialise in PascalCase by default. Every DTO therefore carries explicit
    /// <see cref="JsonPropertyNameAttribute"/> annotations and these options are used directly for
    /// anything written to the NDJSON stream, so the wire format never depends on host configuration.
    /// </para>
    /// <para>
    /// Nulls are omitted: over 34,500 records, absent optional fields would otherwise cost megabytes.
    /// </para>
    /// </remarks>
    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.Strict
    };
}
