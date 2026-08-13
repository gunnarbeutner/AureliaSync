using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Configuration;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Storage.Migrations;
using Jellyfin.Plugin.AureliaSync.Wire;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Snapshots;

/// <summary>
/// Brings the plugin database up at startup, builds snapshots, and runs periodic maintenance.
/// </summary>
/// <remarks>
/// Jellyfin registers plugin hosted services before the web host's own, so <see cref="StartAsync"/>
/// runs before Kestrel binds. It therefore starts work and returns immediately; anything that needs
/// the database waits on <see cref="SyncRuntime.WaitAsync"/>.
/// </remarks>
public sealed class SnapshotCoordinator : IHostedService, IDisposable
{
    /// <summary>
    /// How many journal records below the slowest client are kept anyway.
    /// </summary>
    /// <remarks>
    /// A client that acknowledged a moment ago but has not yet reconnected should not be stranded
    /// by a few seconds of timing, and journal records are small.
    /// </remarks>
    public const long JournalSafetyMargin = 500;

    /// <summary>How often maintenance runs.</summary>
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long a closed, expired, or failed session row is kept for diagnostics.</summary>
    public static readonly TimeSpan DeadSessionRetention = TimeSpan.FromDays(7);

    /// <summary>How long acknowledgement receipts are kept. They need only outlive client retries.</summary>
    public static readonly TimeSpan AckReceiptRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// The age beyond which journal records are dropped regardless of who still wants them.
    /// </summary>
    /// <remarks>
    /// The backstop against a journal growing forever because one client stopped returning. Any
    /// client starved by it is marked as needing a fresh snapshot rather than silently skipped.
    /// </remarks>
    public static readonly TimeSpan JournalMaxAge = TimeSpan.FromDays(30);

    private readonly ILogger<SnapshotCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly SyncRuntime _runtime;
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

    private readonly Channel<Guid> _buildQueue =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<Guid, byte> _queued = new ConcurrentDictionary<Guid, byte>();

    private Task? _maintenanceLoop;
    private Task? _buildLoop;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotCoordinator"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="imageProcessor">Used to compute image cache tags.</param>
    /// <param name="runtime">Shared runtime state.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Factory for component loggers.</param>
    public SnapshotCoordinator(
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IImageProcessor imageProcessor,
        SyncRuntime runtime,
        ILogger<SnapshotCoordinator> logger,
        ILoggerFactory loggerFactory)
    {
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _imageProcessor = imageProcessor;
        _runtime = runtime;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Must return promptly: this runs before Kestrel binds its sockets.
        _maintenanceLoop = Task.Run(() => RunMaintenanceLoopAsync(_stopping.Token), CancellationToken.None);
        _buildLoop = Task.Run(() => RunBuildLoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _buildQueue.Writer.TryComplete();

        foreach (var task in new[] { _maintenanceLoop, _buildLoop })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Shutting down.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>
    /// Returns a snapshot for a user, reusing a recent one or starting a build.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the generation exists — a build takes minutes, and the client cannot be
    /// made to wait on a single request for that long. Delivery waits for completion instead.
    /// </remarks>
    /// <param name="userId">The user.</param>
    /// <param name="wireSchema">The negotiated wire schema.</param>
    /// <param name="forceRebuild">Whether to ignore any reusable snapshot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot that will be, or already is, available.</returns>
    public async Task<SnapshotInfo> EnsureSnapshotAsync(
        Guid userId,
        int wireSchema,
        bool forceRebuild,
        CancellationToken cancellationToken = default)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var store = _runtime.Snapshots;

        if (!forceRebuild)
        {
            var window = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(0, configuration.SnapshotReuseWindowMinutes));
            if (store.FindReusable(userId, wireSchema, window) is { } reusable)
            {
                _logger.LogDebug(
                    "AureliaSync: reusing snapshot {Generation} for {User}", reusable.Generation, userId);
                return reusable;
            }

            // A build already under way is joined rather than duplicated, so a second device does
            // not start a second crawl of the same library.
            if (store.FindLatest(userId) is { State: SnapshotInfo.StateBuilding } building)
            {
                return building;
            }
        }

        // The journal head is read BEFORE the build is queued, and therefore before enumeration
        // begins. Everything that changes during the minutes a build takes lands at a strictly
        // higher sequence, so the client receives it after promoting the snapshot rather than
        // losing it in the gap. Reading it afterwards would silently drop that window.
        var baseline = _runtime.Journal.Head();

        var generation = await store.CreateAsync(userId, wireSchema, baseline, cancellationToken)
            .ConfigureAwait(false);

        if (_queued.TryAdd(userId, 0))
        {
            await _buildQueue.Writer.WriteAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "AureliaSync: queued snapshot {Generation} for {User} at journal baseline {Baseline}",
            generation,
            userId,
            baseline);
        return store.Get(generation)!;
    }

    private async Task RunBuildLoopAsync(CancellationToken cancellationToken)
    {
        if (await _runtime.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false)
            != SyncDatabaseHealth.Ok)
        {
            return;
        }

        try
        {
            await foreach (var userId in _buildQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _queued.TryRemove(userId, out _);
                await BuildForAsync(userId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task BuildForAsync(Guid userId, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var store = _runtime.Snapshots;

        var pending = store.FindLatest(userId);
        if (pending is not { State: SnapshotInfo.StateBuilding })
        {
            return;
        }

        var builder = new SnapshotBuilder(
            _libraryManager,
            _userManager,
            _imageProcessor,
            store,
            _loggerFactory.CreateLogger<SnapshotBuilder>());

        try
        {
            await builder.BuildAsync(userId, pending.Generation, configuration, cancellationToken)
                .ConfigureAwait(false);

            // A build writes roughly a megabyte per thousand records; without this the write-ahead
            // log stays at the size of the snapshot until something else happens to check it.
            await _runtime.Database.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await store.FailAsync(pending.Generation, "serverShutdown", null, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // One user's failed build must not stop the worker serving everyone else.
            _logger.LogError(ex, "AureliaSync: snapshot {Generation} failed", pending.Generation);
            await store.FailAsync(pending.Generation, "buildFailed", ex.Message, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        if (!Initialize())
        {
            return;
        }

        try
        {
            // Any snapshot still marked building has no worker behind it, because this process just
            // started. Left alone it would keep sessions waiting for something that is never coming.
            var invalidated = await _runtime.Snapshots
                .InvalidateInterruptedBuildsAsync(cancellationToken).ConfigureAwait(false);

            if (invalidated > 0)
            {
                _logger.LogInformation(
                    "AureliaSync: invalidated {Count} snapshot(s) interrupted by a restart", invalidated);
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogError(ex, "AureliaSync: could not invalidate interrupted snapshots");
        }

        using var timer = new PeriodicTimer(MaintenanceInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RunMaintenanceAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "AureliaSync: maintenance pass failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private bool Initialize()
    {
        var plugin = Plugin.Instance;
        var databasePath = plugin is not null
            ? plugin.DatabasePath
            : System.IO.Path.Combine(_applicationPaths.DataPath, Plugin.DataDirectoryName, "aureliasync.db");

        // The runtime proof that ExcludeAssets=runtime worked: this must resolve to the assembly
        // Jellyfin itself loaded, not a copy beside the plugin. A second copy would mean a second
        // SQLitePCLRaw native provider registration in this process.
        var sqliteAssembly = typeof(SqliteConnection).Assembly;
        _logger.LogInformation(
            "AureliaSync: using {Assembly} loaded from {Location}",
            sqliteAssembly.GetName().FullName,
            string.IsNullOrEmpty(sqliteAssembly.Location) ? "(no file location)" : sqliteAssembly.Location);

        try
        {
            var runner = new MigrationRunner(_logger);
            runner.Run(databasePath);

            var database = new SyncDatabase(databasePath, _loggerFactory.CreateLogger<SyncDatabase>());
            var signingKey = new MetaStore(database)
                .GetOrCreateSigningKeyAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            _runtime.MarkReady(database, runner.TargetVersion, signingKey);

            _logger.LogInformation(
                "AureliaSync: database ready at {Path} (schema version {Version}, wire schema {Wire})",
                databasePath,
                runner.TargetVersion,
                WireSchema.WireSchemaVersionMax);
            return true;
        }
        catch (Exception ex)
        {
            // Fail closed, but never take the server down: Jellyfin keeps running and /status says why.
            _runtime.MarkUnavailable(ex.Message);
            _logger.LogCritical(
                ex,
                "AureliaSync: could not open the plugin database at {Path}; synchronisation is disabled",
                databasePath);
            return false;
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        if (!_runtime.IsUsable)
        {
            return;
        }

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var deadSessionCutoff = now - (long)DeadSessionRetention.TotalMilliseconds;
        var ackCutoff = now - (long)AckReceiptRetention.TotalMilliseconds;
        var subscriptionCutoff = DateTimeOffset.UtcNow
            .AddDays(-Math.Max(1, configuration.SubscriptionExpiryDays)).ToUnixTimeMilliseconds();

        await _runtime.Database.WriteAsync(
            (connection, transaction) =>
            {
                var expired = SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE sessions SET state = 'expired'
                     WHERE expires_at < $now AND state IN ('preparing', 'streaming', 'snapshotComplete');
                    """,
                    ("$now", now));

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    DELETE FROM sessions
                     WHERE state IN ('closed', 'expired', 'failed') AND last_seen_at < $cutoff;
                    """,
                    ("$cutoff", deadSessionCutoff));

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    "DELETE FROM ack_requests WHERE created_at < $cutoff;",
                    ("$cutoff", ackCutoff));

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE subscriptions SET state = 'expired'
                     WHERE last_seen_at < $cutoff AND state = 'active';
                    """,
                    ("$cutoff", subscriptionCutoff));

                // A snapshot is only reclaimed once nothing points at it: no session still using
                // it, and no subscription resting on it. Deleting one out from under a client would
                // turn its next request into an unexplained restart.
                var reclaimed = SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    DELETE FROM snapshot_rows WHERE generation IN (
                        SELECT generation FROM snapshots
                         WHERE expires_at IS NOT NULL AND expires_at < $now
                           AND generation NOT IN (
                               SELECT generation FROM sessions
                                WHERE generation IS NOT NULL
                                  AND state IN ('preparing', 'streaming', 'snapshotComplete'))
                           AND generation NOT IN (
                               SELECT snapshot_generation FROM subscriptions
                                WHERE snapshot_generation IS NOT NULL AND state = 'active'));
                    """,
                    ("$now", now));

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    DELETE FROM snapshots
                     WHERE expires_at IS NOT NULL AND expires_at < $now
                       AND generation NOT IN (SELECT DISTINCT generation FROM snapshot_rows)
                       AND generation NOT IN (
                           SELECT generation FROM sessions
                            WHERE generation IS NOT NULL
                              AND state IN ('preparing', 'streaming', 'snapshotComplete'))
                       AND generation NOT IN (
                           SELECT snapshot_generation FROM subscriptions
                            WHERE snapshot_generation IS NOT NULL AND state = 'active');
                    """,
                    ("$now", now));

                if (expired > 0 || reclaimed > 0)
                {
                    _logger.LogInformation(
                        "AureliaSync: maintenance expired {Sessions} session(s) and reclaimed {Rows} snapshot row(s)",
                        expired,
                        reclaimed);
                }
            },
            cancellationToken).ConfigureAwait(false);

        await ReclaimJournalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Trims the journal and marks anyone it left behind.
    /// </summary>
    /// <remarks>
    /// The order matters. Records are reclaimed only up to the slowest active client, then the age
    /// backstop runs, and only then are starved clients marked — so a client is never marked for a
    /// gap that the same pass was about to avoid creating.
    /// </remarks>
    private async Task ReclaimJournalAsync(CancellationToken cancellationToken)
    {
        var journal = _runtime.Journal;

        var reclaimed = await journal.ReclaimAsync(JournalSafetyMargin, cancellationToken).ConfigureAwait(false);
        var aged = await journal
            .TrimOlderThanAsync(DateTimeOffset.UtcNow - JournalMaxAge, cancellationToken)
            .ConfigureAwait(false);

        var starved = await journal.MarkStarvedSubscriptionsAsync(cancellationToken).ConfigureAwait(false);

        if (reclaimed > 0 || aged > 0)
        {
            _logger.LogInformation(
                "AureliaSync: reclaimed {Reclaimed} consumed and {Aged} aged journal record(s); head {Head}, floor {Floor}",
                reclaimed,
                aged,
                journal.Head(),
                journal.Floor());
        }

        if (starved > 0)
        {
            // Loud on purpose: each of these is a client about to pay for a full resynchronisation.
            _logger.LogWarning(
                "AureliaSync: {Count} client(s) fell behind the journal and now require a fresh snapshot",
                starved);
        }
    }
}
