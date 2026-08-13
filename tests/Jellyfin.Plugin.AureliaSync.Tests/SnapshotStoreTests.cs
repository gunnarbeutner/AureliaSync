using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Storage.Migrations;
using Jellyfin.Plugin.AureliaSync.Wire;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncDatabase _database;
    private readonly SnapshotStore _store;
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public SnapshotStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aureliasync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "aureliasync.db");

        new MigrationRunner(NullLogger.Instance).Run(path);
        _database = new SyncDatabase(path, NullLogger<SyncDatabase>.Instance);
        _store = new SnapshotStore(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SnapshotRow Row(long ordinal, string? entityType = "track") => new SnapshotRow(
        ordinal,
        WireKind.ItemUpsert,
        entityType,
        ordinal.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
        Encoding.UTF8.GetBytes($"{{\"id\":\"{ordinal}\",\"name\":\"Track {ordinal}\"}}"),
        null);

    [Fact]
    public async Task ANewSnapshotStartsBuildingAndIsNotStreamable()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);

        var info = _store.Get(generation)!;
        Assert.Equal(SnapshotInfo.StateBuilding, info.State);
        Assert.False(info.IsComplete);
        Assert.Equal(UserA, info.UserId);
    }

    [Fact]
    public async Task RowsComeBackInOrdinalOrderWithTheirPayloadsIntact()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(generation, Enumerable.Range(1, 100).Select(i => Row(i)).ToList());

        var rows = _store.ReadAfter(generation, 0, 1000, long.MaxValue);

        Assert.Equal(100, rows.Count);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), rows.Select(r => r.Ordinal));
        Assert.Equal("{\"id\":\"1\",\"name\":\"Track 1\"}", Encoding.UTF8.GetString(rows[0].Payload));
    }

    [Fact]
    public async Task NothingIsDeliverableUntilTheWatermarkIsPublished()
    {
        // The watermark is the only thing standing between a client and a half-written range, so
        // "rows exist" must not be enough on its own.
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(generation, Enumerable.Range(1, 100).Select(i => Row(i)).ToList());

        Assert.Equal(0, _store.Get(generation)!.StreamableThrough);
        Assert.Empty(_store.ReadAfter(generation, 0, 1000, long.MaxValue, _store.Get(generation)!.StreamableThrough));
    }

    [Fact]
    public async Task AWatermarkReleasesOnlyTheRowsBelowIt()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(generation, Enumerable.Range(1, 100).Select(i => Row(i)).ToList());
        await _store.SetStreamableThroughAsync(generation, 40);

        var info = _store.Get(generation)!;
        Assert.Equal(40, info.StreamableThrough);
        Assert.False(info.IsComplete);
        Assert.True(info.HasDeliverableRows);

        var rows = _store.ReadAfter(generation, 0, 1000, long.MaxValue, info.StreamableThrough);
        Assert.Equal(40, rows.Count);
        Assert.Equal(40, rows[^1].Ordinal);
    }

    [Fact]
    public async Task TheWatermarkNeverGoesBackwards()
    {
        // Batches are committed in ascending order, but a retry or a racing writer must not be able
        // to retract rows the client has already been offered.
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(generation, Enumerable.Range(1, 100).Select(i => Row(i)).ToList());

        await _store.SetStreamableThroughAsync(generation, 80);
        await _store.SetStreamableThroughAsync(generation, 20);

        Assert.Equal(80, _store.Get(generation)!.StreamableThrough);
    }

    [Fact]
    public async Task ReadingAfterACursorSkipsWhatWasAlreadyDelivered()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(generation, Enumerable.Range(1, 50).Select(i => Row(i)).ToList());

        var rows = _store.ReadAfter(generation, 30, 1000, long.MaxValue);

        Assert.Equal(20, rows.Count);
        Assert.Equal(31, rows[0].Ordinal);
    }

    [Fact]
    public async Task ReadingIsBoundedByRecordCount()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(generation, Enumerable.Range(1, 500).Select(i => Row(i)).ToList());

        Assert.Equal(10, _store.ReadAfter(generation, 0, 10, long.MaxValue).Count);
    }

    [Fact]
    public async Task ReadingIsBoundedByBytesButAlwaysReturnsAtLeastOneRow()
    {
        // A single record larger than the whole budget must still be delivered, or it becomes a row
        // that can never be sent and the stream wedges permanently at that ordinal.
        var generation = await _store.CreateAsync(UserA, 1, 0);
        var huge = new SnapshotRow(1, WireKind.ItemUpsert, "track", "a", new byte[64 * 1024], null);
        await _store.AppendAsync(generation, new[] { huge, Row(2) });

        var rows = _store.ReadAfter(generation, 0, 1000, 1024);

        Assert.Single(rows);
        Assert.Equal(1, rows[0].Ordinal);
    }

    [Fact]
    public async Task CompletingMakesASnapshotStreamable()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(generation, new[] { Row(1) });
        await _store.CompleteAsync(generation, 1, 42, "sha256:abc", DateTimeOffset.UtcNow.AddHours(48));

        var info = _store.Get(generation)!;
        Assert.True(info.IsComplete);
        Assert.Equal(1, info.RowCount);
        Assert.Equal("sha256:abc", info.Checksum);
        Assert.NotNull(info.CompletedAt);
        Assert.NotNull(info.ExpiresAt);
    }

    [Fact]
    public async Task InterruptedBuildsAreInvalidatedAndTheirPartialRowsDiscarded()
    {
        // A 'building' row that survives a restart has no worker behind it and will never finish.
        // Leaving it would let a session wait forever on a snapshot that is never coming.
        var abandoned = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(abandoned, Enumerable.Range(1, 20).Select(i => Row(i)).ToList());

        var finished = await _store.CreateAsync(UserB, 1, 0);
        await _store.AppendAsync(finished, new[] { Row(1) });
        await _store.CompleteAsync(finished, 1, 10, "sha256:x", DateTimeOffset.UtcNow.AddHours(48));

        var count = await _store.InvalidateInterruptedBuildsAsync();

        Assert.Equal(1, count);
        Assert.Equal(SnapshotInfo.StateInvalidated, _store.Get(abandoned)!.State);
        Assert.Empty(_store.ReadAfter(abandoned, 0, 1000, long.MaxValue));

        // A completed snapshot must be left entirely alone.
        Assert.True(_store.Get(finished)!.IsComplete);
        Assert.Single(_store.ReadAfter(finished, 0, 1000, long.MaxValue));
    }

    [Fact]
    public async Task AFailedBuildRecordsWhyAndStaysUnstreamable()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.FailAsync(generation, "libraryUnavailable", "detail");

        var info = _store.Get(generation)!;
        Assert.Equal(SnapshotInfo.StateFailed, info.State);
        Assert.False(info.IsComplete);
        Assert.Equal("libraryUnavailable", info.ErrorCode);
    }

    [Fact]
    public async Task ReuseFindsAFreshSnapshotForTheSameUserAndSchema()
    {
        // This is what makes a second device cheap: it joins the snapshot the first already paid for.
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.CompleteAsync(generation, 1, 1, "sha256:x", DateTimeOffset.UtcNow.AddHours(48));

        var window = DateTimeOffset.UtcNow.AddMinutes(-360);
        Assert.Equal(generation, _store.FindReusable(UserA, 1, window)!.Generation);

        // Not across users, not across wire schemas, and not once it has aged out.
        Assert.Null(_store.FindReusable(UserB, 1, window));
        Assert.Null(_store.FindReusable(UserA, 2, window));
        Assert.Null(_store.FindReusable(UserA, 1, DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public async Task AnIncompleteSnapshotIsNeverReused()
    {
        await _store.CreateAsync(UserA, 1, 0);

        Assert.Null(_store.FindReusable(UserA, 1, DateTimeOffset.UtcNow.AddHours(-1)));
    }

    [Fact]
    public async Task ProgressIsRecordedWhileBuilding()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.SetProgressAsync(generation, "track", 12_000, 30_224);

        var info = _store.Get(generation)!;
        Assert.Equal("track", info.Phase);
        Assert.Equal(12_000, info.PhaseDone);
        Assert.Equal(30_224, info.PhaseTotal);
    }

    [Fact]
    public async Task DeletingRemovesTheSnapshotAndItsRows()
    {
        var generation = await _store.CreateAsync(UserA, 1, 0);
        await _store.AppendAsync(generation, new[] { Row(1) });

        await _store.DeleteAsync(generation);

        Assert.Null(_store.Get(generation));
        Assert.Empty(_store.ReadAfter(generation, 0, 1000, long.MaxValue));
    }

    [Fact]
    public async Task WritingAndDrainingARealisticSnapshotIsFastEnough()
    {
        // Roughly the shape of the target library: 34,500 rows written in 1,000-row batches, then
        // drained in 1,000-record segments. This is a smoke test for the batching and the index,
        // not a benchmark — it catches an accidental per-row transaction or a missing index, both
        // of which turn minutes of work into hours.
        const int Total = 34_500;
        const int Batch = 1_000;

        var generation = await _store.CreateAsync(UserA, 1, 0);
        var stopwatch = Stopwatch.StartNew();

        for (var offset = 0; offset < Total; offset += Batch)
        {
            var rows = new List<SnapshotRow>(Batch);
            for (var i = 0; i < Batch && offset + i < Total; i++)
            {
                rows.Add(Row(offset + i + 1));
            }

            await _store.AppendAsync(generation, rows);
        }

        var written = stopwatch.Elapsed;
        await _store.CompleteAsync(generation, Total, 0, "sha256:x", DateTimeOffset.UtcNow.AddHours(48));

        stopwatch.Restart();
        long cursor = 0;
        var drained = 0;
        while (true)
        {
            var page = _store.ReadAfter(generation, cursor, 1_000, 8 * 1024 * 1024);
            if (page.Count == 0)
            {
                break;
            }

            drained += page.Count;
            cursor = page[^1].Ordinal;
        }

        Assert.Equal(Total, drained);
        Assert.True(
            written < TimeSpan.FromSeconds(60),
            $"writing {Total} rows took {written} — check batching");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"draining {Total} rows took {stopwatch.Elapsed} — check the (generation, ordinal) index");
    }

    [Fact]
    public async Task SnapshotsOfDifferentGenerationsDoNotBleedIntoEachOther()
    {
        var first = await _store.CreateAsync(UserA, 1, 0);
        var second = await _store.CreateAsync(UserA, 1, 0);

        await _store.AppendAsync(first, new[] { Row(1), Row(2) });
        await _store.AppendAsync(second, new[] { Row(1) });

        Assert.Equal(2, _store.ReadAfter(first, 0, 100, long.MaxValue).Count);
        Assert.Single(_store.ReadAfter(second, 0, 100, long.MaxValue));
        Assert.NotEqual(first, second);
    }
}
