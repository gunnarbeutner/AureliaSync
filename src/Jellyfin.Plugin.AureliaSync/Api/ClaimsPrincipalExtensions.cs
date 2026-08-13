using System;
using System.Linq;
using System.Security.Claims;

namespace Jellyfin.Plugin.AureliaSync.Api;

/// <summary>
/// Reads Jellyfin's authentication claims.
/// </summary>
/// <remarks>
/// Jellyfin's own <c>ClaimsPrincipalExtensions</c> lives in <c>Jellyfin.Api</c>, which is not
/// published to NuGet and so is unavailable to plugins. The claim type strings below are the stable
/// contract from <c>Jellyfin.Api/Constants/InternalClaimTypes.cs</c>.
/// </remarks>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Claim carrying the authenticated user's identifier.
    /// </summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>
    /// Claim carrying the calling device's identifier.
    /// </summary>
    public const string DeviceIdClaim = "Jellyfin-DeviceId";

    /// <summary>
    /// Claim carrying the calling client's name.
    /// </summary>
    public const string ClientClaim = "Jellyfin-Client";

    /// <summary>
    /// Claim indicating the request authenticated with an API key rather than a user token.
    /// </summary>
    public const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    /// <summary>
    /// Gets the authenticated user's identifier.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>
    /// The user identifier, or null when the request carries no usable user scope — which includes
    /// API-key authentication, since an API key is not bound to any particular user and must never
    /// be allowed to read one user's library or listening data.
    /// </returns>
    public static Guid? GetUserId(this ClaimsPrincipal? principal)
    {
        if (principal is null || principal.IsApiKey())
        {
            return null;
        }

        var raw = FindClaim(principal, UserIdClaim);
        if (string.IsNullOrEmpty(raw) || !Guid.TryParse(raw, out var userId) || userId.Equals(Guid.Empty))
        {
            return null;
        }

        return userId;
    }

    /// <summary>
    /// Gets a value indicating whether the request authenticated with an API key.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>True when the request used an API key.</returns>
    public static bool IsApiKey(this ClaimsPrincipal? principal)
    {
        var raw = FindClaim(principal, IsApiKeyClaim);
        return !string.IsNullOrEmpty(raw)
            && bool.TryParse(raw, out var isApiKey)
            && isApiKey;
    }

    /// <summary>
    /// Gets the calling device's identifier, when present.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <returns>The device identifier, or null.</returns>
    public static string? GetDeviceId(this ClaimsPrincipal? principal) =>
        FindClaim(principal, DeviceIdClaim);

    private static string? FindClaim(ClaimsPrincipal? principal, string type) =>
        principal?.Claims
            .FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
