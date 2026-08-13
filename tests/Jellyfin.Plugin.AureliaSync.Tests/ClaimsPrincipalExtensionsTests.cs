using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Plugin.AureliaSync.Api;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

/// <summary>
/// The user identity these tests pin down is the only thing separating one user's library and
/// listening data from another's, so the negative cases matter more than the positive one.
/// </summary>
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal PrincipalWith(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity("Test");
        foreach (var (type, value) in claims)
        {
            identity.AddClaim(new Claim(type, value));
        }

        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void ReadsTheUserIdClaim()
    {
        var expected = Guid.NewGuid();
        var principal = PrincipalWith((ClaimsPrincipalExtensions.UserIdClaim, expected.ToString("N")));

        Assert.Equal(expected, principal.GetUserId());
    }

    [Fact]
    public void MatchesTheClaimTypeCaseInsensitively()
    {
        var expected = Guid.NewGuid();
        var principal = PrincipalWith(("jellyfin-userid", expected.ToString("D")));

        Assert.Equal(expected, principal.GetUserId());
    }

    [Fact]
    public void ApiKeyAuthenticationHasNoUserScope()
    {
        // An API key authenticates a caller but binds them to no user. Serving it a user-scoped
        // library or its listening data would be a cross-user data leak.
        var principal = PrincipalWith(
            (ClaimsPrincipalExtensions.UserIdClaim, Guid.NewGuid().ToString("N")),
            (ClaimsPrincipalExtensions.IsApiKeyClaim, "true"));

        Assert.True(principal.IsApiKey());
        Assert.Null(principal.GetUserId());
    }

    [Fact]
    public void EmptyGuidIsRejected()
    {
        var principal = PrincipalWith((ClaimsPrincipalExtensions.UserIdClaim, Guid.Empty.ToString("N")));

        Assert.Null(principal.GetUserId());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000000000000000000000000")]
    public void MalformedUserIdsAreRejected(string value)
    {
        var principal = PrincipalWith((ClaimsPrincipalExtensions.UserIdClaim, value));

        Assert.Null(principal.GetUserId());
    }

    [Fact]
    public void MissingClaimIsRejected()
    {
        Assert.Null(PrincipalWith().GetUserId());
        Assert.Null(((ClaimsPrincipal?)null).GetUserId());
    }

    [Fact]
    public void NonBooleanApiKeyClaimIsNotTreatedAsAnApiKey()
    {
        var principal = PrincipalWith((ClaimsPrincipalExtensions.IsApiKeyClaim, "yes"));

        Assert.False(principal.IsApiKey());
    }
}
