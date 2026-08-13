using System;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AureliaSync.Storage;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

public class CheckpointTokenTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly Guid User = Guid.Parse("6f3a1b2c-3d4e-5f60-7182-93a4b5c6d7e8");
    private const string Client = "aurelia-test-client";

    [Fact]
    public void RoundTripsThePosition()
    {
        var token = CheckpointToken.Issue(Key, User, Client, 17, 4200);

        Assert.Equal(
            CheckpointToken.Rejection.None,
            CheckpointToken.TryValidate(Key, token, User, Client, out var generation, out var ordinal));
        Assert.Equal(17, generation);
        Assert.Equal(4200, ordinal);
    }

    [Fact]
    public void TamperingWithThePayloadIsDetected()
    {
        var token = CheckpointToken.Issue(Key, User, Client, 17, 4200);
        var forged = CheckpointToken.Issue(Key, User, Client, 17, 999_999);

        // Splice a different payload onto the original signature — the obvious forgery attempt.
        var spliced = forged[..forged.IndexOf('.')] + token[token.IndexOf('.')..];

        Assert.Equal(
            CheckpointToken.Rejection.BadSignature,
            CheckpointToken.TryValidate(Key, spliced, User, Client, out _, out _));
    }

    [Fact]
    public void ADifferentKeyDoesNotValidate()
    {
        var token = CheckpointToken.Issue(Key, User, Client, 1, 1);
        var otherKey = RandomNumberGenerator.GetBytes(32);

        Assert.Equal(
            CheckpointToken.Rejection.BadSignature,
            CheckpointToken.TryValidate(otherKey, token, User, Client, out _, out _));
    }

    [Fact]
    public void ATokenIssuedToAnotherUserIsRejected()
    {
        // A correct signature only proves the server minted the token; it says nothing about who
        // for. Without this check any user could replay another's token and read their position.
        var token = CheckpointToken.Issue(Key, User, Client, 1, 1);

        Assert.Equal(
            CheckpointToken.Rejection.WrongOwner,
            CheckpointToken.TryValidate(Key, token, Guid.NewGuid(), Client, out _, out _));
    }

    [Fact]
    public void ATokenIssuedToAnotherClientIsRejected()
    {
        // Two devices of the same user advance independent checkpoints; neither may resume from
        // the other's position.
        var token = CheckpointToken.Issue(Key, User, Client, 1, 1);

        Assert.Equal(
            CheckpointToken.Rejection.WrongOwner,
            CheckpointToken.TryValidate(Key, token, User, "some-other-client", out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData(".")]
    [InlineData("a.")]
    [InlineData(".b")]
    [InlineData("!!!.???")]
    public void MalformedTokensAreRejectedWithoutThrowing(string? token)
    {
        var thrown = Record.Exception(
            () => CheckpointToken.TryValidate(Key, token, User, Client, out _, out _));

        Assert.Null(thrown);
        Assert.NotEqual(
            CheckpointToken.Rejection.None,
            CheckpointToken.TryValidate(Key, token, User, Client, out _, out _));
    }

    [Fact]
    public void AWellSignedTokenForAVanishedGenerationIsAccepted()
    {
        // This is the ordinary case after snapshot retention expires, not an attack. The caller
        // sees the generation no longer exists and starts a fresh snapshot; rejecting the token
        // outright would surface an error to a client that did nothing wrong.
        var token = CheckpointToken.Issue(Key, User, Client, 999_999, 5);

        Assert.Equal(
            CheckpointToken.Rejection.None,
            CheckpointToken.TryValidate(Key, token, User, Client, out var generation, out _));
        Assert.Equal(999_999, generation);
    }

    [Fact]
    public void TokensAreUrlSafe()
    {
        // Not interpolated into a URL today, but it is opaque state a client may put anywhere.
        var token = CheckpointToken.Issue(Key, User, Client, 17, 4200);

        foreach (var c in token)
        {
            var safe = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.';

            Assert.True(safe, $"token contains unsafe character '{c}'");
        }
    }

    [Fact]
    public void IssuingRejectsAClientIdThatWouldBreakTheEncoding()
    {
        // The payload is separator-delimited, so an identifier containing the separator would
        // shift every following field. The API layer validates the charset; this is the backstop.
        Assert.Throws<ArgumentException>(
            () => CheckpointToken.Issue(Key, User, "bad|client|id", 1, 1));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("aurelia-test-client", true)]
    [InlineData("2E7A9E1C4B0F4D26A1C3F5B7D9E0A2C4", true)]
    [InlineData("has spaces here", false)]
    [InlineData("has|pipe", false)]
    [InlineData("has/slash", false)]
    [InlineData(null, false)]
    public void ClientIdentifierValidationMatchesWhatTheTokenCanCarry(string? clientId, bool expected)
    {
        Assert.Equal(expected, ClientIdentifier.IsValid(clientId));
    }
}
