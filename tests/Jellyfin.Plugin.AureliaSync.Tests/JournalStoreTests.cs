using System;
using System.Collections.Generic;
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

public sealed class JournalStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncDatabase _database;
    private readonly JournalStore _journal;
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public JournalStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aureliasync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "aureliasync.db");

        new MigrationRunner(NullLogger.Instance).Run(path);
        _database = new SyncDatabase(path, NullLogger<SyncDatabase>.Instance);
        _journal = new JournalStore(_database);
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

    private static JournalRecord Record(Guid user, string id = "a", string? groupKey = null) =>
        new JournalRecord(
            user.ToString("N"), WireKind.ItemUpsert, WireEntityType.Track, id, 1,
            Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\"}}"), groupKey);

    private static JournalRecord Broadcast(string id) =>
        new JournalRecord(
            JournalStore.BroadcastScope, WireKind.ItemDelete, WireEntityType.Track, id, 1,
            Encoding.UTF8.GetBytes($"{{\"id\":\"{id}\"}}"));

    private async Task SubscriptionAsync(Guid user, string client, long ackSequence, string state = "active")
    {
        await _database.WriteAsync((connection, transaction) => SyncDatabase.ExecuteWithParameters(
            connection,
            transaction,
            """
            INSERT INTO subscriptions (user_id, client_id, ack_sequence, snapshot_acked, state,
                                       created_at, last_seen_at, expires_at)
            VALUES ($user, $client, $ack, 1, $state, 0, 0, 0)
            ON CONFLICT(user_id, client_id) DO UPDATE SET ack_sequence = excluded.ack_sequence,
                                                          state = excluded.state;
            """,
            ("$user", user.ToString("N")),
            ("$client", client),
            ("$ack", ackSequence),
            ("$state", state)));
    }

    private string State(Guid user, string client)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM subscriptions WHERE user_id = $u AND client_id = $c;";
        command.Parameters.AddWithValue("$u", user.ToString("N"));
        command.Parameters.AddWithValue("$c", client);
        return (string)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task AnEmptyJournalHasNoHeadOrFloor()
    {
        Assert.Equal(0, _journal.Head());
        Assert.Equal(0, _journal.Floor());
        Assert.Equal(0, await _journal.AppendAsync(Array.Empty<JournalRecord>()));
    }

    [Fact]
    public async Task SequencesAreAssignedMonotonically()
    {
        var first = await _journal.AppendAsync(new[] { Record(UserA), Record(UserA) });
        var second = await _journal.AppendAsync(new[] { Record(UserA) });

        Assert.Equal(2, first);
        Assert.Equal(3, second);
        Assert.Equal(3, _journal.Head());
        Assert.Equal(1, _journal.Floor());
    }

    [Fact]
    public async Task AUserOnlySeesTheirOwnRecordsPlusBroadcasts()
    {
        // This is the whole basis of cross-user isolation in change mode.
        await _journal.AppendAsync(new[] { Record(UserA, "a1"), Record(UserB, "b1"), Broadcast("gone") });

        var forA = _journal.ReadAfter(UserA, 0, long.MaxValue, 100, long.MaxValue);
        var forB = _journal.ReadAfter(UserB, 0, long.MaxValue, 100, long.MaxValue);

        Assert.Equal(new[] { "a1", "gone" }, forA.Select(r => r.EntityId));
        Assert.Equal(new[] { "b1", "gone" }, forB.Select(r => r.EntityId));
    }

    [Fact]
    public async Task ReadingIsBoundedByTheSessionUpperBound()
    {
        // A session fixes its upper bound when it opens, so records written while it drains are
        // held back rather than moving the finish line and preventing it ever catching up.
        await _journal.AppendAsync(Enumerable.Range(1, 10).Select(i => Record(UserA, $"a{i}")).ToList());

        var rows = _journal.ReadAfter(UserA, 0, 4, 100, long.MaxValue);

        Assert.Equal(4, rows.Count);
        Assert.Equal(4, rows[^1].Ordinal);
    }

    [Fact]
    public async Task ReadingResumesAfterACursor()
    {
        await _journal.AppendAsync(Enumerable.Range(1, 10).Select(i => Record(UserA, $"a{i}")).ToList());

        var rows = _journal.ReadAfter(UserA, 7, long.MaxValue, 100, long.MaxValue);

        Assert.Equal(3, rows.Count);
        Assert.Equal(8, rows[0].Ordinal);
    }

    [Fact]
    public async Task GroupKeysSurviveTheRoundTrip()
    {
        // The segment writer relies on these to keep a playlist in one segment.
        await _journal.AppendAsync(new[]
        {
            Record(UserA, "e1", groupKey: "playlist-a"),
            Record(UserA, "e2", groupKey: "playlist-a")
        });

        var rows = _journal.ReadAfter(UserA, 0, long.MaxValue, 100, long.MaxValue);
        Assert.All(rows, r => Assert.Equal("playlist-a", r.GroupKey));
    }

    [Fact]
    public async Task HighestVisibleIgnoresOtherUsersRecords()
    {
        // Used to decide whether a change session has anything to deliver. Counting another user's
        // records would leave a client looping forever waiting for records it can never receive.
        await _journal.AppendAsync(new[] { Record(UserA, "a1") });
        await _journal.AppendAsync(new[] { Record(UserB, "b1"), Record(UserB, "b2") });

        Assert.Equal(1, _journal.HighestVisible(UserA, long.MaxValue));
        Assert.Equal(3, _journal.HighestVisible(UserB, long.MaxValue));
    }

    [Fact]
    public async Task ReclaimNeverDeletesBelowASubscriptionThatStillNeedsIt()
    {
        // The critical retention property. Deleting under a client leaves it silently missing
        // changes while believing it is current — far worse than keeping records too long.
        await _journal.AppendAsync(Enumerable.Range(1, 20).Select(i => Record(UserA, $"a{i}")).ToList());
        await SubscriptionAsync(UserA, "fast", 18);
        await SubscriptionAsync(UserB, "slow", 5);

        await _journal.ReclaimAsync(safetyMargin: 0);

        // The slow client is at 5, so nothing above 5 may go.
        Assert.Equal(6, _journal.Floor());
        Assert.Equal(20, _journal.Head());
    }

    [Fact]
    public async Task TheSafetyMarginKeepsExtraRecords()
    {
        await _journal.AppendAsync(Enumerable.Range(1, 20).Select(i => Record(UserA, $"a{i}")).ToList());
        await SubscriptionAsync(UserA, "only", 10);

        await _journal.ReclaimAsync(safetyMargin: 5);

        // Would have cut at 10; the margin holds it at 5.
        Assert.Equal(6, _journal.Floor());
    }

    [Fact]
    public async Task ReclaimKeepsEverythingWhenNoSubscriptionIsActive()
    {
        await _journal.AppendAsync(Enumerable.Range(1, 5).Select(i => Record(UserA, $"a{i}")).ToList());

        Assert.Equal(0, await _journal.ReclaimAsync(safetyMargin: 0));
        Assert.Equal(5, _journal.Count());
    }

    [Fact]
    public async Task AnExpiredSubscriptionDoesNotHoldTheJournalOpen()
    {
        await _journal.AppendAsync(Enumerable.Range(1, 20).Select(i => Record(UserA, $"a{i}")).ToList());
        await SubscriptionAsync(UserA, "gone", 2, state: "expired");
        await SubscriptionAsync(UserB, "here", 15);

        await _journal.ReclaimAsync(safetyMargin: 0);

        Assert.Equal(16, _journal.Floor());
    }

    [Fact]
    public async Task ASubscriptionBelowTheFloorIsMarkedRatherThanSkipped()
    {
        // Silently resuming above a gap would leave the client believing it was current while
        // missing everything in between. A fresh snapshot is expensive but correct.
        await _journal.AppendAsync(Enumerable.Range(1, 20).Select(i => Record(UserA, $"a{i}")).ToList());
        await SubscriptionAsync(UserA, "stalled", 2);
        await _journal.TrimOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(5));
        await _journal.AppendAsync(Enumerable.Range(1, 3).Select(i => Record(UserA, $"b{i}")).ToList());

        var marked = await _journal.MarkStarvedSubscriptionsAsync();

        Assert.Equal(1, marked);
        Assert.Equal("snapshotRequired", State(UserA, "stalled"));
    }

    [Fact]
    public async Task ASubscriptionExactlyAtTheFloorIsStillContiguous()
    {
        // Position floor-1 means the next record needed is the oldest retained. Marking it would
        // force a pointless full resync.
        await _journal.AppendAsync(Enumerable.Range(1, 10).Select(i => Record(UserA, $"a{i}")).ToList());
        await SubscriptionAsync(UserA, "edge", 4);
        await _journal.ReclaimAsync(safetyMargin: 0);

        Assert.Equal(5, _journal.Floor());
        Assert.Equal(0, await _journal.MarkStarvedSubscriptionsAsync());
        Assert.Equal("active", State(UserA, "edge"));
    }

    [Fact]
    public async Task TrimmingByAgeIsTheBackstopForAClientThatNeverReturns()
    {
        await _journal.AppendAsync(Enumerable.Range(1, 5).Select(i => Record(UserA, $"a{i}")).ToList());

        Assert.Equal(0, await _journal.TrimOlderThanAsync(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal(5, await _journal.TrimOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Equal(0, _journal.Count());
    }

    [Fact]
    public async Task PayloadsComeBackByteForByte()
    {
        var payload = Encoding.UTF8.GetBytes("{\"id\":\"x\",\"name\":\"Jóga\"}");
        await _journal.AppendAsync(new[]
        {
            new JournalRecord(UserA.ToString("N"), WireKind.ItemUpsert, "track", "x", 1, payload)
        });

        var rows = _journal.ReadAfter(UserA, 0, long.MaxValue, 10, long.MaxValue);
        Assert.Equal(payload, rows[0].Payload);
    }
}
