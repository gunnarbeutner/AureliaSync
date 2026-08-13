using System.Text.Encodings.Web;
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
    /// The client decodes with <c>.useDefaultKeys</c> and the protocol's key casing is irregular
    /// (<c>artistId</c> but <c>artistIDs</c>, <c>playlistID</c>, <c>albumImageTag</c>), so every
    /// DTO property is pinned with an explicit <see cref="JsonPropertyNameAttribute"/>.
    /// </para>
    /// <para>
    /// <see cref="JsonSerializerOptions.PropertyNamingPolicy"/> is deliberately left null rather
    /// than set to camel case. A camel-case policy would paper over a forgotten annotation by
    /// producing a plausible-looking key; leaving it null makes the omission emit PascalCase, which
    /// the golden-file tests catch immediately. It is a tripwire, not an oversight.
    /// </para>
    /// <para>
    /// Nulls are omitted: over 34,500 records, absent optional fields would otherwise cost
    /// megabytes. See <c>docs/PROTOCOL.md</c> for the one place where absence carries meaning —
    /// user data is applied with COALESCE, so a cleared value must be sent explicitly.
    /// </para>
    /// </remarks>
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            PropertyNamingPolicy = null,
            NumberHandling = JsonNumberHandling.Strict,

            // The default encoder escapes every non-ASCII character, so "Björk" would go out as
            // "Björk" — nearly double the bytes for any name with an accent, across a library
            // full of them. The relaxed encoder still escapes everything JSON requires; "unsafe"
            // refers to embedding output in HTML, which an NDJSON response body never is.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        options.Converters.Add(new Iso8601MillisecondConverter());
        return options;
    }
}
