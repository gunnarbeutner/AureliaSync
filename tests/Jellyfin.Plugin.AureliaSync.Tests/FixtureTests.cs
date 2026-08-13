using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AureliaSync.Wire;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

/// <summary>
/// Keeps the published conformance fixtures in step with the implementation.
/// </summary>
/// <remarks>
/// The fixtures under <c>docs/fixtures</c> are what the client agent tests its decoder against
/// without needing a live server. Generating them from the real wire types and asserting the
/// committed files match means the wire format cannot change without the fixture diff showing up
/// in the same commit.
/// <para>
/// Set <c>AURELIASYNC_UPDATE_FIXTURES=1</c> to rewrite them after a deliberate protocol change.
/// </para>
/// </remarks>
public class FixtureTests
{
    private static bool ShouldUpdate =>
        string.Equals(
            Environment.GetEnvironmentVariable("AURELIASYNC_UPDATE_FIXTURES"),
            "1",
            StringComparison.Ordinal);

    [Fact]
    public void PublishedFixturesMatchWhatTheCodeProduces()
    {
        var directory = FixtureBuilder.FixtureDirectory();
        var stale = new List<string>();

        foreach (var (name, expected) in FixtureBuilder.All())
        {
            var path = Path.Combine(directory, name);

            if (ShouldUpdate)
            {
                File.WriteAllText(path, expected);
                continue;
            }

            if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), expected, StringComparison.Ordinal))
            {
                stale.Add(name);
            }
        }

        Assert.True(
            stale.Count == 0,
            $"Fixtures out of date: {string.Join(", ", stale)}. "
            + "Re-run with AURELIASYNC_UPDATE_FIXTURES=1 and commit the result, then tell the client agent.");
    }

    [Fact]
    public void EveryFixtureLineIsAJsonObjectWithAKind()
    {
        // The client decodes every line — records included — into a shape requiring a string kind.
        foreach (var (name, content) in FixtureBuilder.All())
        {
            foreach (var line in Lines(content))
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
                Assert.True(
                    document.RootElement.TryGetProperty("kind", out var kind),
                    $"{name}: line without a kind: {line}");
                Assert.Equal(JsonValueKind.String, kind.ValueKind);
            }
        }
    }

    [Fact]
    public void EveryRecordLineCarriesACursor()
    {
        // A record without a cursor throws a raw decoding error on the client and kills the sync.
        foreach (var (name, content) in FixtureBuilder.All())
        {
            foreach (var line in Lines(content))
            {
                using var document = JsonDocument.Parse(line);
                var kind = document.RootElement.GetProperty("kind").GetString()!;

                if (kind is WireKind.SegmentBegin or WireKind.Error)
                {
                    continue;
                }

                Assert.True(
                    document.RootElement.TryGetProperty("cursor", out var cursor)
                    && cursor.ValueKind == JsonValueKind.String
                    && cursor.GetString()!.Length > 0,
                    $"{name}: {kind} line without a cursor");
            }
        }
    }

    [Fact]
    public void OnlyKindsTheClientToleratesAppear()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            WireKind.SegmentBegin, WireKind.SegmentEnd, WireKind.Error,
            WireKind.ItemUpsert, WireKind.PlaylistReplace, WireKind.UserDataUpsert, WireKind.ItemDelete
        };

        foreach (var (name, content) in FixtureBuilder.All())
        {
            foreach (var line in Lines(content))
            {
                using var document = JsonDocument.Parse(line);
                var kind = document.RootElement.GetProperty("kind").GetString()!;
                Assert.True(allowed.Contains(kind), $"{name}: unexpected kind '{kind}'");
            }
        }
    }

    [Fact]
    public void TheCompleteSnapshotIsBracketedAndReportsCaughtUpExactlyOnce()
    {
        var lines = Lines(FixtureBuilder.All()["snapshot-complete.ndjson"]).ToList();

        Assert.Equal(WireKind.SegmentBegin, KindOf(lines[0]));
        Assert.Equal(WireKind.SegmentEnd, KindOf(lines[^1]));
        Assert.Equal(1, lines.Count(l => KindOf(l) == WireKind.SegmentBegin));
        Assert.Equal(1, lines.Count(l => KindOf(l) == WireKind.SegmentEnd));

        using var end = JsonDocument.Parse(lines[^1]);
        Assert.True(end.RootElement.GetProperty("caughtUp").GetBoolean());

        // recordCount excludes the two control lines.
        Assert.Equal(lines.Count - 2, end.RootElement.GetProperty("recordCount").GetInt32());
    }

    [Fact]
    public void ThePartialSegmentDoesNotClaimToBeComplete()
    {
        var lines = Lines(FixtureBuilder.All()["snapshot-partial.ndjson"]).ToList();

        using var end = JsonDocument.Parse(lines[^1]);
        Assert.Equal(WireKind.SegmentEnd, KindOf(lines[^1]));
        Assert.False(end.RootElement.GetProperty("caughtUp").GetBoolean());
    }

    [Fact]
    public void TheErrorFixtureHasNoSegmentEnd()
    {
        // Absence of segment.end is exactly how a client detects a segment it must discard.
        var lines = Lines(FixtureBuilder.All()["stream-error.ndjson"]).ToList();

        Assert.DoesNotContain(lines, l => KindOf(l) == WireKind.SegmentEnd);
        Assert.Equal(WireKind.Error, KindOf(lines[^1]));
    }

    [Fact]
    public void PlaylistEntriesForOnePlaylistShareASegmentAndAreDenselyPositioned()
    {
        var positions = Lines(FixtureBuilder.All()["snapshot-complete.ndjson"])
            .Select(l => JsonDocument.Parse(l))
            .Where(d => d.RootElement.GetProperty("kind").GetString() == WireKind.PlaylistReplace)
            .Select(d => d.RootElement.GetProperty("payload").GetProperty("position").GetInt32())
            .OrderBy(p => p)
            .ToList();

        Assert.Equal(new[] { 0, 1 }, positions);
    }

    [Fact]
    public void TheChangesFixtureUsesJournalCursorsAndCarriesATombstone()
    {
        var lines = Lines(FixtureBuilder.All()["changes-segment.ndjson"]).ToList();

        using var begin = JsonDocument.Parse(lines[0]);
        Assert.Equal("changes", begin.RootElement.GetProperty("mode").GetString());

        // Journal cursors address a different sequence from snapshot ones and must decode as such.
        foreach (var line in lines.Skip(1))
        {
            using var document = JsonDocument.Parse(line);
            var cursor = document.RootElement.GetProperty("cursor").GetString();
            Assert.True(Jellyfin.Plugin.AureliaSync.Streaming.Cursor.TryDecode(cursor, out var decoded));
            Assert.Equal(Jellyfin.Plugin.AureliaSync.Streaming.Cursor.JournalKind, decoded.Kind);
        }

        // Tombstones appear only here, never in a snapshot.
        Assert.Contains(lines, l => KindOf(l) == WireKind.ItemDelete);
        Assert.DoesNotContain(
            Lines(FixtureBuilder.All()["snapshot-complete.ndjson"]),
            l => KindOf(l) == WireKind.ItemDelete);
    }

    [Fact]
    public void NonAsciiSurvivesAsUtf8RatherThanEscapes()
    {
        var content = FixtureBuilder.All()["snapshot-complete.ndjson"];

        Assert.Contains("Björk", content, StringComparison.Ordinal);
        Assert.Contains("Jóga", content, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00", content, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Lines(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string KindOf(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("kind").GetString()!;
    }
}
