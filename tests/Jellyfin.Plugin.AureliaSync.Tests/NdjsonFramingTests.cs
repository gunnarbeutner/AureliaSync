using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Streaming;
using Jellyfin.Plugin.AureliaSync.Wire;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

public class NdjsonFramingTests
{
    private const long Generation = 17;

    private static SnapshotRow Row(long ordinal, string? groupKey = null, int payloadSize = 32) =>
        new SnapshotRow(
            ordinal,
            groupKey is null ? WireKind.ItemUpsert : WireKind.PlaylistReplace,
            groupKey is null ? WireEntityType.Track : null,
            ordinal.ToString("x8", System.Globalization.CultureInfo.InvariantCulture),
            // The ordinal is in the payload so that two rows are never byte-identical, which keeps
            // ordering and cursor assertions honest.
            Encoding.UTF8.GetBytes(
                "{\"id\":" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"pad\":\"" + new string('x', Math.Max(1, payloadSize)) + "\"}"),
            groupKey);

    private static SegmentBegin Begin() => new SegmentBegin
    {
        SessionId = "s1",
        Generation = Generation,
        ServerTime = new DateTimeOffset(2026, 8, 13, 15, 48, 55, 123, TimeSpan.Zero)
    };

    private static async Task<(string Text, SegmentOutcome Outcome)> WriteAsync(
        IReadOnlyList<SnapshotRow> rows,
        long afterOrdinal = 0,
        long upperBound = long.MaxValue,
        bool ready = true,
        int maxRecords = 1000,
        long maxBytes = 8 * 1024 * 1024,
        Func<long, Task>? onIssued = null)
    {
        using var stream = new MemoryStream();
        var outcome = await NdjsonSegmentWriter.WriteAsync(
            stream, Begin(), rows, afterOrdinal, upperBound, ready, maxRecords, maxBytes,
            TimeSpan.FromMinutes(5), onIssued, CancellationToken.None);

        return (Encoding.UTF8.GetString(stream.ToArray()), outcome);
    }

    private static List<JsonDocument> Parse(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();

    private static string Kind(JsonDocument d) => d.RootElement.GetProperty("kind").GetString()!;

    [Fact]
    public async Task ASegmentIsBracketedByItsControlLines()
    {
        var (text, outcome) = await WriteAsync(new[] { Row(1), Row(2), Row(3) }, upperBound: 3);
        var lines = Parse(text);

        Assert.Equal(WireKind.SegmentBegin, Kind(lines[0]));
        Assert.Equal(WireKind.SegmentEnd, Kind(lines[^1]));
        Assert.Equal(3, outcome.RecordCount);
        Assert.Equal(5, lines.Count);
    }

    [Fact]
    public async Task EveryLineIsTerminatedAndNoRecordContainsARawNewline()
    {
        var (text, _) = await WriteAsync(new[] { Row(1), Row(2) });

        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(4, text.Count(c => c == '\n'));

        // Line-delimited framing survives only if nothing embeds a literal newline.
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.DoesNotContain('\n', line);
            JsonDocument.Parse(line).Dispose();
        }
    }

    [Fact]
    public async Task EveryRecordCarriesACursorThatDecodesToItsOrdinal()
    {
        // A record without a cursor throws a raw decoding error on the client and kills the sync.
        var (text, _) = await WriteAsync(new[] { Row(4), Row(9) });

        var records = Parse(text).Where(d => Kind(d) == WireKind.ItemUpsert).ToList();
        Assert.Equal(2, records.Count);

        foreach (var (record, expected) in records.Zip(new long[] { 4, 9 }))
        {
            var cursor = record.RootElement.GetProperty("cursor").GetString();
            Assert.True(Cursor.TryDecode(cursor, out var decoded));
            Assert.Equal(expected, decoded.Ordinal);
            Assert.Equal(Generation, decoded.Generation);
        }
    }

    [Fact]
    public async Task PayloadsArePassedThroughUnchanged()
    {
        var payload = Encoding.UTF8.GetBytes("{\"id\":\"abc\",\"name\":\"Björk\",\"n\":1.5}");
        var row = new SnapshotRow(1, WireKind.ItemUpsert, "track", "abc", payload);

        var (text, _) = await WriteAsync(new[] { row });
        var record = Parse(text).First(d => Kind(d) == WireKind.ItemUpsert);

        Assert.Equal("abc", record.RootElement.GetProperty("payload").GetProperty("id").GetString());
        Assert.Equal("Björk", record.RootElement.GetProperty("payload").GetProperty("name").GetString());
    }

    [Fact]
    public async Task TheRecordLimitEndsASegmentEarly()
    {
        var (_, outcome) = await WriteAsync(
            Enumerable.Range(1, 50).Select(i => Row(i)).ToList(), maxRecords: 10);

        Assert.Equal(10, outcome.RecordCount);
        Assert.Equal(SegmentOutcome.StopMaxRecords, outcome.StopReason);
        Assert.False(outcome.CaughtUp);
    }

    [Fact]
    public async Task TheByteBudgetCountsFramingNotJustPayloads()
    {
        // The client counts the whole body against its limit, so budgeting only payload bytes
        // would consistently overshoot.
        var (text, outcome) = await WriteAsync(
            Enumerable.Range(1, 200).Select(i => Row(i, payloadSize: 512)).ToList(),
            maxBytes: 16 * 1024);

        Assert.Equal(SegmentOutcome.StopMaxBytes, outcome.StopReason);
        Assert.True(outcome.TotalBytes > outcome.PayloadBytes, "framing must be counted");
        Assert.Equal(Encoding.UTF8.GetByteCount(text), outcome.TotalBytes);
    }

    [Fact]
    public async Task ASingleOversizedRecordIsStillDelivered()
    {
        // Otherwise it becomes a row that can never be sent and the stream wedges at that ordinal.
        var huge = new SnapshotRow(1, WireKind.ItemUpsert, "track", "a", new byte[128 * 1024], null);

        var (_, outcome) = await WriteAsync(new[] { huge, Row(2) }, maxBytes: 1024);

        Assert.Equal(1, outcome.RecordCount);
        Assert.Equal(1, outcome.LastOrdinal);
    }

    [Fact]
    public async Task APlaylistIsNeverSplitAcrossSegments()
    {
        // The client clears a playlist's membership and reinserts only what the segment contained,
        // so half a playlist means the other half is silently dropped.
        var rows = new List<SnapshotRow> { Row(1) };
        rows.AddRange(Enumerable.Range(2, 20).Select(i => Row(i, groupKey: "playlist-a")));
        rows.AddRange(Enumerable.Range(22, 20).Select(i => Row(i, groupKey: "playlist-b")));

        // A limit that would otherwise cut in the middle of playlist-a.
        var (text, outcome) = await WriteAsync(rows, maxRecords: 5);

        var delivered = Parse(text)
            .Where(d => Kind(d) == WireKind.PlaylistReplace)
            .Select(d => d.RootElement.GetProperty("entityId").GetString())
            .ToList();

        // Either all 20 of playlist-a's entries or none, never a partial group.
        Assert.Equal(21, outcome.RecordCount);
        Assert.Equal(20, delivered.Count);
        Assert.DoesNotContain(Parse(text), d => Kind(d) == WireKind.PlaylistReplace
            && d.RootElement.GetProperty("entityId").GetString() == "0000001b");
    }

    [Fact]
    public async Task CaughtUpIsOnlySetWhenTheSnapshotIsFinishedAndFullyDelivered()
    {
        var rows = new[] { Row(1), Row(2) };

        // Everything delivered, but the snapshot is still building.
        var (_, building) = await WriteAsync(rows, upperBound: 2, ready: false);
        Assert.False(building.CaughtUp);

        // Ready, but more remains beyond this segment.
        var (_, partial) = await WriteAsync(rows, upperBound: 99, ready: true);
        Assert.False(partial.CaughtUp);

        // Ready and complete.
        var (_, done) = await WriteAsync(rows, upperBound: 2, ready: true);
        Assert.True(done.CaughtUp);
        Assert.Equal(SegmentOutcome.StopUpperBound, done.StopReason);
    }

    [Fact]
    public async Task AnEmptySegmentIsStillValidlyFramed()
    {
        // This is what a client receives while a snapshot is still being built. It must be a
        // complete, parseable segment, or the client discards it and never makes progress.
        var (text, outcome) = await WriteAsync(
            Array.Empty<SnapshotRow>(), afterOrdinal: 42, upperBound: 99, ready: false);

        var lines = Parse(text);
        Assert.Equal(2, lines.Count);
        Assert.Equal(WireKind.SegmentBegin, Kind(lines[0]));
        Assert.Equal(WireKind.SegmentEnd, Kind(lines[1]));
        Assert.Equal(0, outcome.RecordCount);
        Assert.False(outcome.CaughtUp);

        // The cursor echoes where the client asked to continue from, so it does not go backwards.
        Assert.True(Cursor.TryDecode(lines[1].RootElement.GetProperty("cursor").GetString(), out var cursor));
        Assert.Equal(42, cursor.Ordinal);
    }

    [Fact]
    public async Task TheIssuedOrdinalIsRecordedBeforeTheClosingLineIsWritten()
    {
        // If the connection dies during the final flush, the client may still have received records
        // it will acknowledge. Recording afterwards would make that acknowledgement invalid.
        var order = new List<string>();

        using var stream = new MemoryStream();
        await NdjsonSegmentWriter.WriteAsync(
            stream,
            Begin(),
            new[] { Row(1), Row(2) },
            0,
            2,
            true,
            1000,
            8 * 1024 * 1024,
            TimeSpan.FromMinutes(5),
            ordinal =>
            {
                order.Add("issued:" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                order.Add("bytesSoFar:" + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("issued:2", order[0]);

        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains(WireKind.SegmentEnd, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheClosingLineReportsCountsThatMatchTheBody()
    {
        var (text, outcome) = await WriteAsync(new[] { Row(1), Row(2), Row(3) }, upperBound: 3);
        var end = Parse(text)[^1].RootElement;

        Assert.Equal(outcome.RecordCount, end.GetProperty("recordCount").GetInt32());
        Assert.Equal(outcome.PayloadBytes, end.GetProperty("byteCount").GetInt64());
        Assert.Equal(
            Parse(text).Count(d => Kind(d) is not (WireKind.SegmentBegin or WireKind.SegmentEnd)),
            end.GetProperty("recordCount").GetInt32());
    }

    [Fact]
    public async Task TheClosingLineAdvertisesNoDigest()
    {
        // Digests were removed because they could only detect corruption between hashing and
        // verification — a window the segment framing, gzip's CRC and TLS already cover — while
        // being unable to detect a server-side mistake at all, having been computed from the same
        // bytes. This asserts the field is gone rather than merely unused.
        var (text, _) = await WriteAsync(new[] { Row(1), Row(2) }, upperBound: 2);
        var end = Parse(text)[^1].RootElement;

        Assert.False(end.TryGetProperty("aggregateChecksum", out _));
    }

    [Fact]
    public async Task AnErrorLineEndsTheBodyWithoutAClosingLine()
    {
        // Absence of segment.end is exactly how the client knows to discard what it read.
        using var stream = new MemoryStream();
        await NdjsonSegmentWriter.WriteErrorAsync(
            stream,
            new ErrorLine { Code = "snapshotInvalidated", Message = "m", CorrelationId = "c" },
            CancellationToken.None);

        var lines = Parse(Encoding.UTF8.GetString(stream.ToArray()));
        Assert.Single(lines);
        Assert.Equal(WireKind.Error, Kind(lines[0]));
    }
}
