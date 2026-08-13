using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AureliaSync.Configuration;
using Jellyfin.Plugin.AureliaSync.Projection;
using Jellyfin.Plugin.AureliaSync.Snapshots;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Wire;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Journal;

/// <summary>
/// Turns Jellyfin's library and user-data events into durable journal records.
/// </summary>
/// <remarks>
/// <para>
/// Records are materialised here, at write time, using the same reader and projector the snapshot
/// uses — so a change delivered as a delta describes an item byte-for-byte identically to a
/// snapshot of the same item. That equality is what makes it safe for a client to apply either.
/// </para>
/// <para>
/// Intake is bounded and lossy by design. If events outrun the worker, the excess is dropped and a
/// reconciliation is requested rather than growing memory without limit: a missed event is
/// recoverable because reconciliation will find it, whereas an unbounded queue is not recoverable
/// at all. This is the trade that lets a library scan run without the plugin becoming a liability.
/// </para>
/// </remarks>
public sealed class JournalWriter : IHostedService, IDisposable
{
    /// <summary>How long related events are gathered before being written.</summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often a single item's playback position may be journalled.
    /// </summary>
    /// <remarks>
    /// <c>PlaybackProgress</c> fires every few seconds per playing client. Rate-limiting it at
    /// intake keeps the storm out of the queue entirely rather than filtering it afterwards, and a
    /// resume position still propagates between devices within the window. The final position is
    /// not lost: playback stopping raises a different reason, which is never suppressed.
    /// </remarks>
    public static readonly TimeSpan ProgressWindow = TimeSpan.FromSeconds(30);

    private const int QueueCapacity = 8192;
    private const int MaxBatch = 512;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly SyncRuntime _runtime;
    private readonly ILogger<JournalWriter> _logger;
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

    private readonly Channel<ChangeEvent> _queue = Channel.CreateBounded<ChangeEvent>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    private readonly ConcurrentDictionary<(Guid User, Guid Item), long> _lastProgress =
        new ConcurrentDictionary<(Guid, Guid), long>();

    /// <summary>
    /// Attached after construction: resolving IUserDataManager while the container is still
    /// being built pulls in a slice of Jellyfin's own service graph.
    /// </summary>
    private IUserDataManager? _userDataManager;

    private BaseItemFactsReader? _reader;
    private Task? _worker;
    private bool _subscribed;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalWriter"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="imageProcessor">Used to compute image cache tags.</param>
    /// <param name="runtime">Shared runtime state.</param>
    /// <param name="logger">Logger.</param>
    public JournalWriter(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IImageProcessor imageProcessor,
        SyncRuntime runtime,
        ILogger<JournalWriter> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _imageProcessor = imageProcessor;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether events were dropped and a reconciliation is owed.
    /// </summary>
    public bool ReconciliationRequested { get; private set; }

    /// <summary>
    /// Clears the reconciliation request, once one has run.
    /// </summary>
    public void ClearReconciliationRequest() => ReconciliationRequested = false;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _reader = new BaseItemFactsReader(_imageProcessor, _logger);

        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        UserDataManagerHook(subscribe: true);
        _subscribed = true;

        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribed)
        {
            _libraryManager.ItemAdded -= OnItemChanged;
            _libraryManager.ItemUpdated -= OnItemChanged;
            _libraryManager.ItemRemoved -= OnItemRemoved;
            UserDataManagerHook(subscribe: false);
            _subscribed = false;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();

        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
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
    /// Attaches the user-data manager, which cannot be constructor-injected without creating a
    /// dependency cycle through Jellyfin's own service graph.
    /// </summary>
    /// <param name="userDataManager">The manager to observe.</param>
    public void Attach(IUserDataManager userDataManager)
    {
        _userDataManager = userDataManager;
        if (_subscribed)
        {
            UserDataManagerHook(subscribe: true);
        }
    }

    private void UserDataManagerHook(bool subscribe)
    {
        if (_userDataManager is null)
        {
            return;
        }

        if (subscribe)
        {
            _userDataManager.UserDataSaved += OnUserDataSaved;
        }
        else
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is null || !IsInteresting(e.Item))
        {
            return;
        }

        Publish(new ChangeEvent(ChangeEventKind.ItemChanged, e.Item.Id, Guid.Empty, EntityTypeOf(e.Item)));
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is null || !IsInteresting(e.Item))
        {
            return;
        }

        // The entity type is captured now because after this there is nothing left to read it from.
        Publish(new ChangeEvent(ChangeEventKind.ItemRemoved, e.Item.Id, Guid.Empty, EntityTypeOf(e.Item)));
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e?.Item is null || e.UserId.Equals(Guid.Empty) || !IsInteresting(e.Item))
        {
            return;
        }

        if (e.SaveReason == UserDataSaveReason.PlaybackProgress && !ProgressWindowElapsed(e.UserId, e.Item.Id))
        {
            return;
        }

        Publish(new ChangeEvent(ChangeEventKind.UserDataChanged, e.Item.Id, e.UserId, null));
    }

    /// <summary>
    /// Returns whether this item's playback position may be journalled again yet.
    /// </summary>
    private bool ProgressWindowElapsed(Guid userId, Guid itemId)
    {
        var now = Environment.TickCount64;
        var key = (userId, itemId);

        if (_lastProgress.TryGetValue(key, out var last) && now - last < ProgressWindow.TotalMilliseconds)
        {
            return false;
        }

        _lastProgress[key] = now;

        // Bounded, because otherwise it grows by one entry per item ever played. The contents are
        // a rate-limiting hint, so discarding them costs at most one extra record.
        if (_lastProgress.Count > 4096)
        {
            _lastProgress.Clear();
        }

        return true;
    }

    private void Publish(ChangeEvent change)
    {
        if (_queue.Writer.TryWrite(change))
        {
            return;
        }

        // Dropped. Reconciliation will find whatever was lost, which is why the queue is allowed to
        // be bounded in the first place.
        if (!ReconciliationRequested)
        {
            ReconciliationRequested = true;
            _logger.LogWarning(
                "AureliaSync: change events are arriving faster than they can be journalled; "
                + "the excess is being dropped and a reconciliation has been requested");
        }
    }

    private static bool IsInteresting(BaseItem item) =>
        item is Audio or MusicAlbum or MusicArtist or MusicGenre or Playlist;

    private static string? EntityTypeOf(BaseItem item) => item switch
    {
        Audio => WireEntityType.Track,
        MusicAlbum => WireEntityType.Album,
        MusicArtist => WireEntityType.Artist,
        MusicGenre => WireEntityType.Genre,
        Playlist => WireEntityType.Playlist,
        _ => null
    };

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (await _runtime.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false)
            != SyncDatabaseHealth.Ok)
        {
            return;
        }

        try
        {
            while (await _queue.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var batch = await GatherAsync(cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    continue;
                }

                try
                {
                    await FlushAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad batch must not stop the journal. Reconciliation covers what was lost.
                    ReconciliationRequested = true;
                    _logger.LogError(ex, "AureliaSync: failed to journal a batch of changes");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Collects events for the debounce window, coalescing repeats.
    /// </summary>
    /// <remarks>
    /// A library scan touches the same item several times in quick succession. Coalescing before
    /// anything is written is safe and is the only place it is safe: once a sequence has been
    /// issued the record is immutable, because a client may already have read it.
    /// </remarks>
    private async Task<Dictionary<(ChangeEventKind Kind, Guid ItemId, Guid UserId), ChangeEvent>> GatherAsync(
        CancellationToken cancellationToken)
    {
        var batch = new Dictionary<(ChangeEventKind Kind, Guid ItemId, Guid UserId), ChangeEvent>();
        var deadline = DateTimeOffset.UtcNow + DebounceWindow;

        while (batch.Count < MaxBatch)
        {
            while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var change))
            {
                batch[change.CoalesceKey] = change;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero || batch.Count >= MaxBatch)
            {
                break;
            }

            try
            {
                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return batch;
    }

    private async Task FlushAsync(
        Dictionary<(ChangeEventKind Kind, Guid ItemId, Guid UserId), ChangeEvent> batch,
        CancellationToken cancellationToken)
    {
        // Only users who already hold a checkpoint need journal records. Anyone else will take a
        // snapshot when they first connect, so writing for them is pure waste.
        var subscribers = _runtime.Sessions.ActiveSubscriberIds();
        if (subscribers.Count == 0)
        {
            return;
        }

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var records = new List<JournalRecord>();

        var users = _userManager.GetUsers()
            .Where(u => subscribers.Contains(u.Id))
            .ToList();

        foreach (var change in batch.Values.OrderBy(c => c.Kind))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (change.Kind)
            {
                case ChangeEventKind.ItemRemoved:
                    records.Add(Tombstone(change));
                    break;

                case ChangeEventKind.ItemChanged:
                    AppendItemChange(records, change, users);
                    break;

                case ChangeEventKind.UserDataChanged:
                    AppendUserDataChange(records, change, users);
                    break;
            }
        }

        if (records.Count == 0)
        {
            return;
        }

        var head = await _runtime.Journal.AppendAsync(records, cancellationToken).ConfigureAwait(false);

        if (configuration.VerboseDiagnostics)
        {
            _logger.LogInformation(
                "AureliaSync: journalled {Records} record(s) from {Changes} change(s); head is now {Head}",
                records.Count,
                batch.Count,
                head);
        }
    }

    /// <summary>
    /// Builds a tombstone, which every subscriber receives.
    /// </summary>
    /// <remarks>
    /// Deliberately not visibility-filtered. Asking whether a vanishing item was visible is
    /// unreliable, and the two mistakes are not equally bad: a tombstone a client cannot match
    /// deletes nothing, whereas one wrongly withheld leaves a deleted track in that client's
    /// library permanently. The only thing disclosed is that some identifier stopped existing.
    /// </remarks>
    private static JournalRecord Tombstone(ChangeEvent change)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new { id = change.ItemId.ToString("N") }, WireSchema.JsonOptions);

        return new JournalRecord(
            JournalStore.BroadcastScope,
            WireKind.ItemDelete,
            change.EntityType,
            change.ItemId.ToString("N"),
            WireSchema.WireSchemaVersionMax,
            payload);
    }

    private void AppendItemChange(List<JournalRecord> records, ChangeEvent change, List<User> users)
    {
        var item = _libraryManager.GetItemById(change.ItemId);
        if (item is null)
        {
            // Edited and then deleted inside one window. The removal event carries the tombstone.
            return;
        }

        foreach (var user in users)
        {
            if (!item.IsVisible(user, false))
            {
                continue;
            }

            var projector = BuildProjector(item);

            if (item is Playlist playlist)
            {
                AppendPlaylist(records, playlist, user, projector);
                continue;
            }

            var facts = _reader!.Read(item, user.Id);
            var (payload, entityType) = Project(projector, item, facts);
            if (payload is null || entityType is null)
            {
                continue;
            }

            records.Add(new JournalRecord(
                user.Id.ToString("N"),
                WireKind.ItemUpsert,
                entityType,
                facts.Id.ToString("N"),
                WireSchema.WireSchemaVersionMax,
                payload));
        }
    }

    /// <summary>
    /// Re-materialises a playlist's whole membership.
    /// </summary>
    /// <remarks>
    /// Jellyfin raises no membership event, so any touch of a playlist is treated as "the contents
    /// may have changed". The records share a group key, which the segment writer uses to keep them
    /// in one segment — the client clears the playlist and reinserts only what a segment contained,
    /// so a split would silently drop half of it.
    /// </remarks>
    private void AppendPlaylist(
        List<JournalRecord> records, Playlist playlist, User user, PayloadProjector projector)
    {
        var entries = PlaylistMembershipReader.Read(playlist, user, user.Id, _reader!);
        var facts = _reader!.Read(playlist, user.Id);
        var groupKey = playlist.Id.ToString("N");

        records.Add(new JournalRecord(
            user.Id.ToString("N"),
            WireKind.ItemUpsert,
            WireEntityType.Playlist,
            groupKey,
            WireSchema.WireSchemaVersionMax,
            JsonSerializer.SerializeToUtf8Bytes(projector.Playlist(facts, entries.Count), WireSchema.JsonOptions)));

        foreach (var entry in entries)
        {
            var payload = projector.PlaylistEntry(entry.Facts, playlist.Id, entry.EntryId, entry.Position);

            records.Add(new JournalRecord(
                user.Id.ToString("N"),
                WireKind.PlaylistReplace,
                null,
                payload.Id,
                WireSchema.WireSchemaVersionMax,
                JsonSerializer.SerializeToUtf8Bytes(payload, WireSchema.JsonOptions),
                groupKey));
        }
    }

    private void AppendUserDataChange(List<JournalRecord> records, ChangeEvent change, List<User> users)
    {
        var user = users.FirstOrDefault(u => u.Id.Equals(change.UserId));
        if (user is null)
        {
            return;
        }

        var item = _libraryManager.GetItemById(change.ItemId);
        if (item is null || !item.IsVisible(user, false))
        {
            return;
        }

        var facts = _reader!.Read(item, user.Id);
        var payload = BuildProjector(item).UserData(facts);

        // A user-data row that has been reset to nothing has no record to send, but the client
        // applies user data with COALESCE and would keep the old values. Send explicit zeroes.
        payload ??= new Wire.Payloads.UserDataPayload
        {
            Id = facts.Id.ToString("N"),
            IsFavorite = false,
            PlayCount = 0,
            PlaybackPositionTicks = 0
        };

        records.Add(new JournalRecord(
            user.Id.ToString("N"),
            WireKind.UserDataUpsert,
            null,
            payload.Id,
            WireSchema.WireSchemaVersionMax,
            JsonSerializer.SerializeToUtf8Bytes(payload, WireSchema.JsonOptions)));
    }

    /// <summary>
    /// Builds a projector holding just the lookups this item needs.
    /// </summary>
    /// <remarks>
    /// The snapshot builds library-wide maps once because it touches everything. A delta touches
    /// one item, so the maps are built per item instead — two lookups rather than a full crawl.
    /// </remarks>
    private PayloadProjector BuildProjector(BaseItem item)
    {
        var names = new List<string>();
        if (item is Audio audio)
        {
            names.AddRange(audio.Artists);
            names.AddRange(audio.AlbumArtists);
        }
        else if (item is MusicAlbum album)
        {
            names.AddRange(album.Artists);
            names.AddRange(album.AlbumArtists);
        }

        var artistIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (names.Count > 0)
        {
            foreach (var (name, artists) in _libraryManager.GetArtists(names.Distinct().ToList()))
            {
                if (artists.Length > 0)
                {
                    artistIds.TryAdd(name, artists[0].Id);
                }
            }
        }

        var albums = new Dictionary<Guid, AlbumSummary>();
        if (item is Audio track)
        {
            var albumId = !track.ParentId.Equals(Guid.Empty) ? track.ParentId : track.AlbumEntity?.Id;
            if (albumId is { } id && _libraryManager.GetItemById(id) is { } albumItem)
            {
                albums[id] = new AlbumSummary(albumItem.Name, _reader!.ReadPrimaryImageTag(albumItem));
            }
        }

        return new PayloadProjector(artistIds, albums, _libraryManager.GetMusicGenreId);
    }

    private ProjectedPayload Project(
        PayloadProjector projector, BaseItem item, ItemFacts facts)
    {
        object? payload = item switch
        {
            Audio => projector.Track(facts),
            MusicAlbum => projector.Album(facts, null),
            MusicArtist => projector.Artist(facts, null, isAlbumArtist: true),
            MusicGenre => projector.Genre(facts.Id, facts.Name, null),
            _ => null
        };

        return payload is null
            ? new ProjectedPayload(null, null)
            : new ProjectedPayload(
                JsonSerializer.SerializeToUtf8Bytes(payload, WireSchema.JsonOptions), EntityTypeOf(item));
    }
}
