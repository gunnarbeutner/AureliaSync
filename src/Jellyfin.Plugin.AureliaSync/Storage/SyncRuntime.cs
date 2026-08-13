using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AureliaSync.Storage;

/// <summary>
/// Holds the plugin's database handle and its startup health, and lets request handlers wait for
/// initialisation to finish.
/// </summary>
/// <remarks>
/// Initialisation is deliberately asynchronous. Jellyfin starts hosted services before Kestrel binds
/// its sockets, so blocking in <c>StartAsync</c> would delay the whole server coming up. Opening and
/// migrating the database therefore happens on a background task, and endpoints wait on
/// <see cref="WaitAsync"/> with a short timeout instead.
/// </remarks>
public sealed class SyncRuntime : IDisposable
{
    private readonly TaskCompletionSource<SyncDatabaseHealth> _ready =
        new TaskCompletionSource<SyncDatabaseHealth>(TaskCreationOptions.RunContinuationsAsynchronously);

    private SyncDatabase? _database;
    private bool _disposed;

    /// <summary>
    /// Gets the current health. Starts as <see cref="SyncDatabaseHealth.Starting"/>.
    /// </summary>
    public SyncDatabaseHealth Health { get; private set; } = SyncDatabaseHealth.Starting;

    /// <summary>
    /// Gets an administrator-facing explanation when <see cref="Health"/> is not
    /// <see cref="SyncDatabaseHealth.Ok"/>.
    /// </summary>
    public string? Diagnostic { get; private set; }

    /// <summary>
    /// Gets the on-disk schema version, or zero when the database is not open.
    /// </summary>
    public int SchemaVersion { get; private set; }

    /// <summary>
    /// Gets a task that completes once initialisation has settled, successfully or not.
    /// </summary>
    public Task<SyncDatabaseHealth> Ready => _ready.Task;

    /// <summary>
    /// Gets the open database.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is not available.</exception>
    public SyncDatabase Database =>
        _database ?? throw new InvalidOperationException(
            "The AureliaSync database is not available: " + (Diagnostic ?? "still starting."));

    /// <summary>
    /// Gets a value indicating whether the database is open and usable.
    /// </summary>
    public bool IsUsable => _database is not null
        && (Health == SyncDatabaseHealth.Ok || Health == SyncDatabaseHealth.Degraded);

    /// <summary>
    /// Records successful initialisation.
    /// </summary>
    /// <param name="database">The opened database.</param>
    /// <param name="schemaVersion">Its schema version.</param>
    public void MarkReady(SyncDatabase database, int schemaVersion)
    {
        _database = database;
        SchemaVersion = schemaVersion;
        Health = SyncDatabaseHealth.Ok;
        Diagnostic = null;
        _ready.TrySetResult(SyncDatabaseHealth.Ok);
    }

    /// <summary>
    /// Records that the database could not be brought up. All sync endpoints will refuse.
    /// </summary>
    /// <param name="diagnostic">Administrator-facing explanation.</param>
    public void MarkUnavailable(string diagnostic)
    {
        Health = SyncDatabaseHealth.Unavailable;
        Diagnostic = diagnostic;
        _ready.TrySetResult(SyncDatabaseHealth.Unavailable);
    }

    /// <summary>
    /// Records that the database is open but needs administrator attention.
    /// </summary>
    /// <param name="diagnostic">Administrator-facing explanation.</param>
    public void MarkDegraded(string diagnostic)
    {
        if (Health == SyncDatabaseHealth.Ok)
        {
            Health = SyncDatabaseHealth.Degraded;
        }

        Diagnostic = diagnostic;
    }

    /// <summary>
    /// Waits for initialisation to settle.
    /// </summary>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The settled health, or <see cref="SyncDatabaseHealth.Starting"/> if the timeout elapsed first.
    /// </returns>
    public async Task<SyncDatabaseHealth> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_ready.Task.IsCompleted)
        {
            return await _ready.Task.ConfigureAwait(false);
        }

        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(_ready.Task, delay).ConfigureAwait(false);

        return completed == _ready.Task
            ? await _ready.Task.ConfigureAwait(false)
            : SyncDatabaseHealth.Starting;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ready.TrySetResult(SyncDatabaseHealth.Unavailable);
        _database?.Dispose();
        _database = null;
    }
}
