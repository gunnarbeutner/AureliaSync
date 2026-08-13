using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Storage.Migrations;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Snapshots;

/// <summary>
/// Brings the plugin database up at startup and runs periodic maintenance.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin registers plugin hosted services before the web host's own, so <see cref="StartAsync"/>
/// runs before Kestrel binds. It therefore starts initialisation and returns immediately; anything
/// that waits for the database waits on <see cref="SyncRuntime.WaitAsync"/> instead.
/// </para>
/// <para>
/// From phase 2 this class also owns the snapshot build queue and its single worker.
/// </para>
/// </remarks>
public sealed class SnapshotCoordinator : IHostedService, IDisposable
{
    /// <summary>
    /// How often maintenance runs.
    /// </summary>
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a closed, expired, or failed session row is retained for diagnostics.
    /// </summary>
    public static readonly TimeSpan DeadSessionRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// How long acknowledgement receipts are retained. They only need to outlive client retries.
    /// </summary>
    public static readonly TimeSpan AckReceiptRetention = TimeSpan.FromDays(7);

    private readonly ILogger<SnapshotCoordinator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly SyncRuntime _runtime;
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

    private Task? _maintenanceLoop;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotCoordinator"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="runtime">Shared runtime state.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="loggerFactory">Factory used to construct the database's own logger.</param>
    public SnapshotCoordinator(
        IApplicationPaths applicationPaths,
        SyncRuntime runtime,
        ILogger<SnapshotCoordinator> logger,
        ILoggerFactory loggerFactory)
    {
        _applicationPaths = applicationPaths;
        _runtime = runtime;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Must return promptly: this runs before Kestrel binds its sockets.
        _maintenanceLoop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_maintenanceLoop is not null)
        {
            try
            {
                await _maintenanceLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down; nothing to report.
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("AureliaSync: maintenance loop did not stop in time");
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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Initialize())
        {
            return;
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
                    // Maintenance is best-effort. One bad tick must not kill the loop.
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
        // Jellyfin itself loaded, not to a copy sitting in the plugin directory. A second copy would
        // mean a second SQLitePCLRaw native provider registration in this process.
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
            _runtime.MarkReady(database, runner.TargetVersion);

            _logger.LogInformation(
                "AureliaSync: database ready at {Path} (schema version {Version})",
                databasePath,
                runner.TargetVersion);
            return true;
        }
        catch (Exception ex)
        {
            // Fail closed, but never take the server down with us: Jellyfin keeps running and the
            // status endpoint reports why sync is unavailable.
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

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var deadSessionCutoff = now - (long)DeadSessionRetention.TotalMilliseconds;
        var ackCutoff = now - (long)AckReceiptRetention.TotalMilliseconds;

        await _runtime.Database.WriteAsync(
            (connection, transaction) =>
            {
                // Expire sessions that have gone quiet. Expiring a session never touches the
                // client's checkpoint, which lives in subscriptions.
                var expired = SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE sessions
                       SET state = 'expired'
                     WHERE expires_at < $now
                       AND state IN ('preparing', 'streaming', 'snapshotComplete');
                    """,
                    ("$now", now));

                var removedSessions = SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    DELETE FROM sessions
                     WHERE state IN ('closed', 'expired', 'failed')
                       AND last_seen_at < $cutoff;
                    """,
                    ("$cutoff", deadSessionCutoff));

                var removedAcks = SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    "DELETE FROM ack_requests WHERE created_at < $cutoff;",
                    ("$cutoff", ackCutoff));

                if (expired > 0 || removedSessions > 0 || removedAcks > 0)
                {
                    _logger.LogInformation(
                        "AureliaSync: maintenance expired {Expired} session(s), removed {Sessions} dead session row(s) and {Acks} ack receipt(s)",
                        expired,
                        removedSessions,
                        removedAcks);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}
