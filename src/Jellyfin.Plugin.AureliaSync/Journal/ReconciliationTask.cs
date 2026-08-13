using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AureliaSync.Storage;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Journal;

/// <summary>
/// The scheduled task that runs reconciliation.
/// </summary>
/// <remarks>
/// Registered as a Jellyfin scheduled task so an administrator can see when it last ran and trigger
/// it by hand — which matters, because it is the recovery path for changes that events missed.
/// </remarks>
public sealed class ReconciliationTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly SyncRuntime _runtime;
    private readonly JournalWriter _journalWriter;
    private readonly ILogger<ReconciliationTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="imageProcessor">Used to compute image cache tags.</param>
    /// <param name="runtime">Shared runtime state.</param>
    /// <param name="journalWriter">The journal writer, so a dropped-event flag can be cleared.</param>
    /// <param name="logger">Logger.</param>
    public ReconciliationTask(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IImageProcessor imageProcessor,
        SyncRuntime runtime,
        JournalWriter journalWriter,
        ILogger<ReconciliationTask> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _imageProcessor = imageProcessor;
        _runtime = runtime;
        _journalWriter = journalWriter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Reconcile Aurelia Sync";

    /// <inheritdoc />
    public string Key => "AureliaSyncReconcile";

    /// <inheritdoc />
    public string Description =>
        "Compares the music library against what Aurelia Sync has already sent, and records anything "
        + "Jellyfin's change events missed — playlist edits, artist and genre changes, and anything "
        + "altered while the plugin was not running.";

    /// <inheritdoc />
    public string Category => "Aurelia Sync";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_runtime.IsUsable)
        {
            _logger.LogInformation("AureliaSync: skipping reconciliation, the plugin database is unavailable");
            progress?.Report(100);
            return;
        }

        var service = new ReconciliationService(
            _libraryManager, _userManager, _imageProcessor, _runtime, _logger);

        await service.RunAsync(progress, cancellationToken).ConfigureAwait(false);

        // Whatever the queue dropped has now been found by comparison, so the debt is settled.
        _journalWriter.ClearReconciliationRequest();
    }
}
