using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Storage.Migrations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

/// <summary>
/// The counters exist so that a sync which failed on a client is legible from the server. These
/// assert they actually accumulate, because a counter that silently stays at zero is worse than no
/// counter at all — it reads as "the server sent nothing".
/// </summary>
public sealed class SessionCounterTests : IDisposable
{
    private readonly string _directory;
    private readonly SyncDatabase _database;
    private readonly SessionStore _sessions;
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public SessionCounterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "aureliasync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "aureliasync.db");

        new MigrationRunner(NullLogger.Instance).Run(path);
        _database = new SyncDatabase(path, NullLogger<SyncDatabase>.Instance);
        _sessions = new SessionStore(_database);
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

    private async Task<SessionInfo> NewSessionAsync(string clientId = "cli-counter", string? reason = null)
    {
        var session = new SessionInfo
        {
            Id = SessionStore.NewSessionId(),
            UserId = User,
            ClientId = clientId,
            Mode = "snapshot",
            ProtocolVersion = 1,
            WireSchema = 1,
            Generation = 7,
            UpperBound = 100,
            State = SessionInfo.StateStreaming,
            Reason = reason,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        };

        await _sessions.CreateAsync(session);
        return session;
    }

    [Fact]
    public async Task CountersStartAtZeroAndAccumulatePerSegment()
    {
        var session = await NewSessionAsync();
        var expires = DateTimeOffset.UtcNow.AddHours(24);

        Assert.Equal(0, _sessions.Get(session.Id)!.SegmentsDelivered);

        await _sessions.RecordIssuedAsync(session.Id, 10, expires, 10, 4_000);
        await _sessions.RecordIssuedAsync(session.Id, 25, expires, 15, 6_500);

        var after = _sessions.Get(session.Id)!;
        Assert.Equal(2, after.SegmentsDelivered);
        Assert.Equal(25, after.RecordsDelivered);
        Assert.Equal(10_500, after.BytesDelivered);
        Assert.Equal(25, after.HighestIssuedOrdinal);
    }

    [Fact]
    public async Task AnEmptySegmentStillCountsAsADeliveryAttempt()
    {
        // Empty segments are how "the snapshot is still building" is reported, and a run consisting
        // only of them is exactly the case someone would be trying to diagnose.
        var session = await NewSessionAsync();
        await _sessions.RecordIssuedAsync(session.Id, 0, DateTimeOffset.UtcNow.AddHours(24), 0, 0);

        var after = _sessions.Get(session.Id)!;
        Assert.Equal(1, after.SegmentsDelivered);
        Assert.Equal(0, after.RecordsDelivered);
    }

    [Fact]
    public async Task TheIssuedOrdinalNeverGoesBackwards()
    {
        // A retried segment re-delivers records the client already had; the counters may double
        // count, but the issued position must not regress or a later acknowledgement would be
        // rejected as beyond what was issued.
        var session = await NewSessionAsync();
        var expires = DateTimeOffset.UtcNow.AddHours(24);

        await _sessions.RecordIssuedAsync(session.Id, 50, expires, 50, 1000);
        await _sessions.RecordIssuedAsync(session.Id, 20, expires, 20, 400);

        Assert.Equal(50, _sessions.Get(session.Id)!.HighestIssuedOrdinal);
    }

    [Fact]
    public async Task AFailureRecordsItsCorrelationIdentifier()
    {
        var session = await NewSessionAsync();
        await _sessions.RecordErrorAsync(session.Id, "serverBusy", "8d2f0c1e4a5b4c6d");

        Assert.Equal("8d2f0c1e4a5b4c6d", _sessions.Get(session.Id)!.LastErrorCorrelation);
    }

    [Fact]
    public async Task TheSnapshotReasonSurvivesARoundTrip()
    {
        var session = await NewSessionAsync(reason: SessionReason.JournalGap);

        Assert.Equal(SessionReason.JournalGap, _sessions.Get(session.Id)!.Reason);
    }

    [Fact]
    public async Task RecentSessionsComeBackNewestFirst()
    {
        await NewSessionAsync("cli-one");
        await Task.Delay(5);
        await NewSessionAsync("cli-two");

        var recent = _sessions.RecentSessions(10);

        Assert.True(recent.Count >= 2);
        Assert.Equal("cli-two", recent[0].ClientId);
    }

    [Fact]
    public async Task DiagnosticsSeeEverySubscriptionIncludingStarvedOnes()
    {
        // A starved subscription is the one an administrator most needs to see, so it must not be
        // filtered out by the same condition that excludes it from being served changes.
        await _database.WriteAsync((connection, transaction) => SyncDatabase.ExecuteWithParameters(
            connection,
            transaction,
            """
            INSERT INTO subscriptions (user_id, client_id, ack_sequence, snapshot_acked, state, reason,
                                       created_at, last_seen_at, expires_at)
            VALUES ($u, 'cli-stalled', 3, 1, 'snapshotRequired', 'journalGap', 0, 0, 0);
            """,
            ("$u", User.ToString("N"))));

        var all = _sessions.AllSubscriptions();
        var stalled = all.Single(row => row.ClientId == "cli-stalled");

        Assert.Equal("journalGap", stalled.Subscription.Reason);
        Assert.False(stalled.Subscription.CanReceiveChanges);
    }
}
