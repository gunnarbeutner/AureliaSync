using System;
using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AureliaSync.Wire;

/// <summary>
/// Serialises timestamps as ISO-8601 UTC with exactly three fractional-second digits.
/// </summary>
/// <remarks>
/// <para>
/// The Aurelia client parses dates with <c>ISO8601DateFormatter</c> and the
/// <c>.withFractionalSeconds</c> option, which accepts <b>exactly three</b> fractional digits or
/// none at all. .NET's default round-trip format emits seven, which that parser rejects outright.
/// </para>
/// <para>
/// This converter is the reason dates are not left to the default serialiser. It is registered on
/// <see cref="WireSchema.JsonOptions"/> and covers <see cref="Nullable{T}"/> automatically.
/// </para>
/// </remarks>
public sealed class Iso8601MillisecondConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>
    /// The one format this protocol emits.
    /// </summary>
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <inheritdoc />
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException("Expected an ISO-8601 timestamp.");
        }

        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        Span<char> buffer = stackalloc char[Format.Length + 8];
        if (value.ToUniversalTime().TryFormat(buffer, out var written, Format, CultureInfo.InvariantCulture))
        {
            writer.WriteStringValue(buffer[..written]);
            return;
        }

        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
    }
}
