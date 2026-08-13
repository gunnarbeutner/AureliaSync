using System;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// An opaque, signed record of how far a client has durably committed.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>only</b> resume state the client sends when opening a session — it never sends a
/// cursor — so the token has to carry the full position by itself.
/// </para>
/// <para>
/// It is signed because it survives the session that issued it and is handed back later by a party
/// that could have edited it. Signing means the server can trust the position without keeping a
/// row for every token it has ever minted.
/// </para>
/// </remarks>
public static class CheckpointToken
{
    /// <summary>Encoding version.</summary>
    public const string Version = "v1";

    private const char Separator = '.';
    private const char FieldSeparator = '|';

    /// <summary>
    /// Why a token was not accepted.
    /// </summary>
    public enum Rejection
    {
        /// <summary>The token was accepted.</summary>
        None = 0,

        /// <summary>Structurally unparseable, or an unknown version.</summary>
        Malformed = 1,

        /// <summary>The signature did not verify — the token was tampered with or forged.</summary>
        BadSignature = 2,

        /// <summary>Correctly signed, but issued to a different user or client.</summary>
        WrongOwner = 3
    }

    /// <summary>
    /// Issues a token recording a durable position.
    /// </summary>
    /// <param name="signingKey">The database's signing key.</param>
    /// <param name="userId">Owning user.</param>
    /// <param name="clientId">Owning client installation.</param>
    /// <param name="generation">Snapshot generation.</param>
    /// <param name="ordinal">Highest ordinal durably committed.</param>
    /// <returns>An opaque token.</returns>
    public static string Issue(byte[] signingKey, Guid userId, string clientId, long generation, long ordinal)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        if (!ClientIdentifier.IsValid(clientId))
        {
            throw new ArgumentException(
                "Client identifier must be validated before a checkpoint token is issued.",
                nameof(clientId));
        }

        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}{FieldSeparator}{userId:N}{FieldSeparator}{clientId}{FieldSeparator}{generation}{FieldSeparator}{ordinal}");

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = HMACSHA256.HashData(signingKey, payloadBytes);

        return string.Concat(
            Base64Url.EncodeToString(payloadBytes),
            Separator.ToString(),
            Base64Url.EncodeToString(signature));
    }

    /// <summary>
    /// Validates a token and extracts the position it records.
    /// </summary>
    /// <remarks>
    /// A token that verifies but names a snapshot generation the server no longer holds is
    /// <b>not</b> a failure here — it returns <see cref="Rejection.None"/> with that generation,
    /// and the caller starts a fresh snapshot. Only forgery and misownership are rejections.
    /// </remarks>
    /// <param name="signingKey">The database's signing key.</param>
    /// <param name="token">The token presented by the client.</param>
    /// <param name="userId">The authenticated user the token must belong to.</param>
    /// <param name="clientId">The client installation the token must belong to.</param>
    /// <param name="generation">The recorded snapshot generation.</param>
    /// <param name="ordinal">The recorded ordinal.</param>
    /// <returns>Why the token was rejected, or <see cref="Rejection.None"/>.</returns>
    public static Rejection TryValidate(
        byte[] signingKey,
        string? token,
        Guid userId,
        string clientId,
        out long generation,
        out long ordinal)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        generation = 0;
        ordinal = 0;

        if (string.IsNullOrEmpty(token))
        {
            return Rejection.Malformed;
        }

        var split = token.IndexOf(Separator, StringComparison.Ordinal);
        if (split <= 0 || split == token.Length - 1)
        {
            return Rejection.Malformed;
        }

        byte[] payloadBytes;
        byte[] presentedSignature;
        try
        {
            payloadBytes = Base64Url.DecodeFromChars(token.AsSpan(0, split));
            presentedSignature = Base64Url.DecodeFromChars(token.AsSpan(split + 1));
        }
        catch (FormatException)
        {
            return Rejection.Malformed;
        }

        var expected = HMACSHA256.HashData(signingKey, payloadBytes);

        // Constant-time: a length-dependent or early-exit comparison would leak the expected
        // signature one byte at a time to anyone able to retry.
        if (!CryptographicOperations.FixedTimeEquals(expected, presentedSignature))
        {
            return Rejection.BadSignature;
        }

        var parts = Encoding.UTF8.GetString(payloadBytes).Split(FieldSeparator);
        if (parts.Length != 5 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
        {
            return Rejection.Malformed;
        }

        if (!Guid.TryParseExact(parts[1], "N", out var tokenUser)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var tokenGeneration)
            || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var tokenOrdinal))
        {
            return Rejection.Malformed;
        }

        // A valid signature only proves the server minted it; it says nothing about who for.
        if (!tokenUser.Equals(userId) || !string.Equals(parts[2], clientId, StringComparison.Ordinal))
        {
            return Rejection.WrongOwner;
        }

        generation = tokenGeneration;
        ordinal = tokenOrdinal;
        return Rejection.None;
    }
}
