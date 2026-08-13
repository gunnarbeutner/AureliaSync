using System;
using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.AureliaSync.Streaming;

/// <summary>
/// An opaque position within a delivery sequence.
/// </summary>
/// <remarks>
/// <para>
/// Encoded as base64url without padding. That alphabet matters: the client interpolates a cursor
/// straight into a query string using <c>.urlQueryAllowed</c>, which does <b>not</b> escape
/// <c>&amp;</c>, <c>=</c>, <c>+</c>, <c>?</c> or <c>#</c>. Standard base64 would produce <c>+</c>
/// and <c>=</c> and silently corrupt the request; base64url produces only
/// <c>A-Z a-z 0-9 - _</c>, all of which survive both a query string and a path segment untouched.
/// </para>
/// <para>
/// Cursors are opaque to clients but are not a capability: every field is re-validated server-side
/// against the owning session. The session identifier is what authorises access.
/// </para>
/// </remarks>
public readonly record struct Cursor
{
    /// <summary>Encoding version, so the shape can change without ambiguity.</summary>
    public const string Version = "1";

    /// <summary>Marks a position within a materialised snapshot.</summary>
    public const string SnapshotKind = "s";

    /// <summary>Marks a position within the change journal. Reserved for protocol v2.</summary>
    public const string JournalKind = "j";

    /// <summary>
    /// Initializes a new instance of the <see cref="Cursor"/> struct.
    /// </summary>
    /// <param name="kind"><see cref="SnapshotKind"/> or <see cref="JournalKind"/>.</param>
    /// <param name="generation">Snapshot generation, or zero for journal positions.</param>
    /// <param name="ordinal">Position within the sequence.</param>
    public Cursor(string kind, long generation, long ordinal)
    {
        Kind = kind;
        Generation = generation;
        Ordinal = ordinal;
    }

    /// <summary>Gets the sequence kind.</summary>
    public string Kind { get; }

    /// <summary>Gets the snapshot generation.</summary>
    public long Generation { get; }

    /// <summary>Gets the position within the sequence.</summary>
    public long Ordinal { get; }

    /// <summary>
    /// Creates a cursor addressing a position in a snapshot generation.
    /// </summary>
    /// <param name="generation">Snapshot generation.</param>
    /// <param name="ordinal">Row ordinal.</param>
    /// <returns>The cursor.</returns>
    public static Cursor ForSnapshot(long generation, long ordinal) =>
        new Cursor(SnapshotKind, generation, ordinal);

    /// <summary>
    /// Encodes the cursor for the wire.
    /// </summary>
    /// <returns>A base64url string containing no character needing URL escaping.</returns>
    public string Encode()
    {
        var plain = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}|{Kind}|{Generation}|{Ordinal}");

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(plain));
    }

    /// <summary>
    /// Decodes a cursor produced by <see cref="Encode"/>.
    /// </summary>
    /// <remarks>
    /// Never throws. Malformed input is a client error to be answered with a structured response,
    /// not an exception to escape into Jellyfin's middleware.
    /// </remarks>
    /// <param name="text">The encoded cursor.</param>
    /// <param name="cursor">The decoded cursor when parsing succeeded.</param>
    /// <returns>True when <paramref name="text"/> was a well-formed cursor.</returns>
    public static bool TryDecode(string? text, out Cursor cursor)
    {
        cursor = default;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(decoded).Split('|');
        if (parts.Length != 4 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
        {
            return false;
        }

        var kind = parts[1];
        if (!string.Equals(kind, SnapshotKind, StringComparison.Ordinal)
            && !string.Equals(kind, JournalKind, StringComparison.Ordinal))
        {
            return false;
        }

        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
        {
            return false;
        }

        cursor = new Cursor(kind, generation, ordinal);
        return true;
    }
}
