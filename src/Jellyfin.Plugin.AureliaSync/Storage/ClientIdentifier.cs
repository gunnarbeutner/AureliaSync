using System;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Validates the client-supplied installation identifier.
/// </summary>
/// <remarks>
/// The identifier is chosen by the client, stored in its keychain, and used as part of a database
/// primary key and inside signed checkpoint tokens. Constraining the character set keeps it out of
/// trouble in both places — in particular the separator used by the token encoding.
/// </remarks>
public static class ClientIdentifier
{
    /// <summary>Shortest accepted identifier.</summary>
    public const int MinLength = 8;

    /// <summary>Longest accepted identifier.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Gets a value indicating whether the identifier is acceptable.
    /// </summary>
    /// <param name="clientId">Candidate identifier.</param>
    /// <returns>True when it is well formed.</returns>
    public static bool IsValid(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length < MinLength || clientId.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in clientId)
        {
            var ok = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.';

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
