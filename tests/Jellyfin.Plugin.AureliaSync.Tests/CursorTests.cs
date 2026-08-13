using System;
using Jellyfin.Plugin.AureliaSync.Streaming;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

public class CursorTests
{
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(17L, 34512L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void RoundTrips(long generation, long ordinal)
    {
        var encoded = Cursor.ForSnapshot(generation, ordinal).Encode();

        Assert.True(Cursor.TryDecode(encoded, out var decoded));
        Assert.Equal(generation, decoded.Generation);
        Assert.Equal(ordinal, decoded.Ordinal);
        Assert.Equal(Cursor.SnapshotKind, decoded.Kind);
    }

    [Fact]
    public void EncodesOnlyCharactersThatSurviveAQueryStringAndAPath()
    {
        // The client interpolates cursors into a URL with .urlQueryAllowed, which does NOT escape
        // & = + ? or #. Standard base64 would emit '+' and '=' and silently corrupt the request —
        // '+' in particular decodes back as a space. base64url avoids the whole class of problem.
        for (long generation = 0; generation < 40; generation++)
        {
            for (long ordinal = 0; ordinal < 40; ordinal++)
            {
                var encoded = Cursor.ForSnapshot(generation, ordinal * 7919).Encode();

                foreach (var c in encoded)
                {
                    var safe = (c >= 'A' && c <= 'Z')
                        || (c >= 'a' && c <= 'z')
                        || (c >= '0' && c <= '9')
                        || c == '-' || c == '_';

                    Assert.True(safe, $"cursor '{encoded}' contains unsafe character '{c}'");
                }
            }
        }
    }

    [Fact]
    public void NeverEmitsBase64Padding()
    {
        // Padding is '=', which is meaningful inside a query string.
        for (long ordinal = 0; ordinal < 200; ordinal++)
        {
            Assert.DoesNotContain('=', Cursor.ForSnapshot(1, ordinal).Encode());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!!")]
    [InlineData("////")]
    [InlineData("YWJj")]                    // valid base64url, wrong shape
    [InlineData("MnxzfDF8MQ")]              // version 2
    [InlineData("MXx4fDF8MQ")]              // unknown kind
    [InlineData("MXxzfDF8LTE")]             // negative ordinal
    [InlineData("MXxzfGF8Yg")]              // non-numeric fields
    public void MalformedInputIsRejectedWithoutThrowing(string? text)
    {
        // Cursors arrive straight from a query string. A parse exception here would escape into
        // Jellyfin's exception middleware instead of becoming a structured 400.
        var decoded = Record.Exception(() => Cursor.TryDecode(text, out _));

        Assert.Null(decoded);
        Assert.False(Cursor.TryDecode(text, out _));
    }

    [Fact]
    public void DifferentPositionsProduceDifferentCursors()
    {
        var a = Cursor.ForSnapshot(1, 1).Encode();
        var b = Cursor.ForSnapshot(1, 2).Encode();
        var c = Cursor.ForSnapshot(2, 1).Encode();

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void EncodingIsStableAcrossCalls()
    {
        // Golden value: a change here changes every cursor previously handed out, so it must be a
        // deliberate protocol decision rather than a refactoring accident.
        Assert.Equal("MXxzfDE3fDQyMDA", Cursor.ForSnapshot(17, 4200).Encode());
    }

    [Fact]
    public void JournalCursorsDecodeButAreDistinctFromSnapshotOnes()
    {
        // Reserved for protocol v2; decoding must already distinguish them so a journal cursor is
        // never mistaken for a snapshot position.
        var journal = new Cursor(Cursor.JournalKind, 0, 99).Encode();

        Assert.True(Cursor.TryDecode(journal, out var decoded));
        Assert.Equal(Cursor.JournalKind, decoded.Kind);
        Assert.NotEqual(Cursor.ForSnapshot(0, 99).Encode(), journal);
    }
}
