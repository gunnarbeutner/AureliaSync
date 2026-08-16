using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Journal;

/// <summary>
/// Compares the library against what has been journalled, and repairs the difference.
/// </summary>
/// <remarks>
/// <para>
/// Events alone are not enough. Jellyfin raises nothing for playlist membership, reports artist and
/// genre changes unreliably, and raises nothing at all for edits made while the plugin was stopped.
/// Without a pass like this, those changes are simply never delivered and a client stays quietly
/// wrong until something forces a fresh snapshot.
/// </para>
/// <para>
/// It is also what allows the journal writer's queue to be bounded: dropped events are recoverable
/// precisely because this finds them again.
/// </para>
/// <para>
/// The comparison is deliberately cheap. It stores a hash of each item's wire payload rather than
/// the payload itself, so the inventory stays small and a repair is emitted only when what a client
/// would receive has actually changed — not merely when Jellyfin touched a row.
/// </para>
/// </remarks>
public sealed class ReconciliationService
{
    private readonly IUserManager _userManager;
    private readonly SyncRuntime _runtime;
    private readonly BaseItemFactsReader _reader;
    private readonly LibraryEnumerator _enumerator;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="imageProcessor">Used to compute image cache tags.</param>
    /// <param name="runtime">Shared runtime state.</param>
    /// <param name="logger">Logger.</param>
    public ReconciliationService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IImageProcessor imageProcessor,
        SyncRuntime runtime,
        ILogger logger)
    {
        _userManager = userManager;
        _runtime = runtime;
        _logger = logger;
        _reader = new BaseItemFactsReader(imageProcessor, logger);
        _enumerator = new LibraryEnumerator(libraryManager, logger);
    }

    /// <summary>
    /// Runs one reconciliation pass for every subscribed user.
    /// </summary>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many repair records were journalled.</returns>
    public async Task<int> RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!_runtime.IsUsable)
        {
            return 0;
        }

        var subscribers = _runtime.Sessions.ActiveSubscriberIds();
        if (subscribers.Count == 0)
        {
            // Nobody is listening, so there is nothing to repair. The next client to arrive takes a
            // snapshot, which is by definition current.
            progress?.Report(100);
            return 0;
        }

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var users = _userManager.GetUsers().Where(u => subscribers.Contains(u.Id)).ToList();
        var repaired = 0;
        var index = 0;

        foreach (var user in users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            repaired += await ReconcileUserAsync(user, configuration, cancellationToken).ConfigureAwait(false);

            index++;
            progress?.Report(100.0 * index / Math.Max(1, users.Count));
        }

        if (repaired > 0)
        {
            _logger.LogInformation(
                "AureliaSync: reconciliation journalled {Count} repair(s) that events had missed", repaired);
        }

        progress?.Report(100);
        return repaired;
    }

    private async Task<int> ReconcileUserAsync(
        User user, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var scope = user.Id.ToString("N");
        var known = ReadInventory(scope);

        // An empty inventory means this user has never been compared, which is not the same as
        // "everything changed". Journalling a repair for every item would hand the client the whole
        // catalog as deltas — precisely the full resynchronisation the journal exists to avoid, and
        // wrong besides, since the client's state came from a snapshot that was current when taken.
        // The first pass therefore only records what is there, and later passes diff against it.
        var seeding = known.Count == 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<JournalRecord>();
        var inventory = new List<InventoryRow>();
        var skipped = 0;

        // The watermark is the whole optimisation. Enumerating identifiers is cheap; hydrating is
        // not, and an earlier version skipped only the projection — which saved nothing, because
        // every item had already been loaded by the time the skip was reached. Letting Jellyfin
        // filter by timestamp means an unchanged item is never loaded at all.
        //
        // A null watermark means "never reconciled", so everything is examined.
        var watermark = seeding ? null : ReadWatermark(scope);
        var passStarted = DateTime.UtcNow;

        // The watermark is applied only to tracks, which are where the 30,000 items are.
        var albumIds = _enumerator.EnumerateIds(user, BaseItemKind.MusicAlbum);
        var albumIdSet = albumIds.ToHashSet();
        var artists = _enumerator.Artists(user);
        var projector = new PayloadProjector(
            LibraryEnumerator.ArtistIdsByName(artists), _enumerator.GenreId);

        var batchSize = Math.Max(50, configuration.SnapshotHydrationBatchSize);

        foreach (var batch in LibraryEnumerator.Batch(albumIds, batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in _enumerator.Hydrate(user, batch))
            {
                var revision = Revision(item);
                if (CanSkip(known, seen, inventory, WireEntityType.Album, item.Id, revision))
                {
                    skipped++;
                    continue;
                }

                var facts = _reader.Read(item, user.Id);

                Compare(
                    records,
                    inventory,
                    seen,
                    known,
                    scope,
                    WireEntityType.Album,
                    facts.Id,
                    JsonSerializer.SerializeToUtf8Bytes(projector.Album(facts), WireSchema.JsonOptions),
                    revision);
            }
        }

        var trackIds = _enumerator.EnumerateIds(user, BaseItemKind.Audio);
        var changedTracks = watermark is null
            ? trackIds.ToHashSet()
            : _enumerator.EnumerateIds(user, BaseItemKind.Audio, watermark).ToHashSet();

        // Everything Jellyfin did not touch keeps the checksum it already had, so the inventory
        // stays complete without those items ever being loaded.
        foreach (var id in trackIds.Where(id => !changedTracks.Contains(id)))
        {
            CarryForward(known, seen, inventory, WireEntityType.Track, id, ref skipped);
        }

        foreach (var batch in LibraryEnumerator.Batch(trackIds.Where(changedTracks.Contains).ToList(), batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in _enumerator.Hydrate(user, batch))
            {
                // Tracks are the bulk of the work — 30,000 of them here — so the skip happens before
                // any facts are read, not just before the payload is built.
                var revision = Revision(item);
                if (CanSkip(known, seen, inventory, WireEntityType.Track, item.Id, revision))
                {
                    skipped++;
                    continue;
                }

                var facts = _reader.Read(item, user.Id, albumIdSet);

                Compare(
                    records,
                    inventory,
                    seen,
                    known,
                    scope,
                    WireEntityType.Track,
                    facts.Id,
                    JsonSerializer.SerializeToUtf8Bytes(projector.Track(facts), WireSchema.JsonOptions),
                    revision);
            }
        }

        // Artists and genres are among the main reasons this pass exists: Jellyfin's events for
        // item-by-name entities are unreliable, so drift here would otherwise never be reported.
        var albumArtistIds = _enumerator.AlbumArtistIds(user);
        var artistsChanged = false;
        var artistCountBefore = records.Count;
        foreach (var (item, _) in artists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var facts = _reader.Read(item, user.Id);

            Compare(
                records,
                inventory,
                seen,
                known,
                scope,
                WireEntityType.Artist,
                facts.Id,
                JsonSerializer.SerializeToUtf8Bytes(
                    projector.Artist(facts, albumArtistIds.Contains(item.Id)),
                    WireSchema.JsonOptions));
        }

        artistsChanged = records.Count != artistCountBefore
            || known.Keys.Any(k => k.StartsWith(WireEntityType.Artist + ":", StringComparison.Ordinal)
                && !seen.Contains(k));

        foreach (var (item, _) in _enumerator.Genres(user))
        {
            cancellationToken.ThrowIfCancellationRequested();

            Compare(
                records,
                inventory,
                seen,
                known,
                scope,
                WireEntityType.Genre,
                item.Id,
                JsonSerializer.SerializeToUtf8Bytes(
                    projector.Genre(item.Id, item.Name ?? string.Empty),
                    WireSchema.JsonOptions));
        }

        // Playlists are the reason this pass exists at all: Jellyfin raises no membership event, so
        // a reorder or removal is invisible until something compares the contents.
        foreach (var playlist in _enumerator.Playlists(user, configuration.AudioPlaylistsOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = PlaylistMembershipReader.Read(playlist, user, user.Id, _reader, albumIdSet);
            var facts = _reader.Read(playlist, user.Id);
            var groupKey = playlist.Id.ToString("N");

            // One hash over the whole membership: the question is whether the playlist as a whole
            // still looks the way the client was last told, not which entry moved.
            var membership = JsonSerializer.SerializeToUtf8Bytes(
                entries.Select(e => new { id = e.Facts.Id.ToString("N"), e.Position }).ToList(),
                WireSchema.JsonOptions);

            if (!Changed(known, seen, scope, WireEntityType.Playlist, playlist.Id, membership, inventory))
            {
                continue;
            }

            records.Add(new JournalRecord(
                scope,
                WireKind.ItemUpsert,
                WireEntityType.Playlist,
                groupKey,
                WireSchema.WireSchemaVersionMax,
                JsonSerializer.SerializeToUtf8Bytes(
                    projector.Playlist(facts), WireSchema.JsonOptions)));

            foreach (var entry in entries)
            {
                var payload = projector.PlaylistEntry(entry.Facts, playlist.Id, entry.EntryId, entry.Position);

                records.Add(new JournalRecord(
                    scope,
                    WireKind.PlaylistReplace,
                    null,
                    payload.Id,
                    WireSchema.WireSchemaVersionMax,
                    JsonSerializer.SerializeToUtf8Bytes(payload, WireSchema.JsonOptions),
                    groupKey));
            }
        }

        // Anything previously inventoried and no longer present has been deleted while nobody was
        // listening. Its removal event is long gone, so the tombstone is issued here.
        foreach (var (key, row) in known)
        {
            if (seen.Contains(key))
            {
                continue;
            }

            records.Add(new JournalRecord(
                scope,
                WireKind.ItemDelete,
                row.EntityType,
                row.EntityId,
                WireSchema.WireSchemaVersionMax,
                JsonSerializer.SerializeToUtf8Bytes(new { id = row.EntityId }, WireSchema.JsonOptions)));
        }

        if (seeding)
        {
            _logger.LogInformation(
                "AureliaSync: recorded a reconciliation baseline of {Count} item(s) for one user; "
                + "later passes will report only what has since changed",
                inventory.Count);

            await WriteInventoryAsync(scope, inventory, seen, cancellationToken).ConfigureAwait(false);
            await WriteWatermarkAsync(scope, passStarted, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (records.Count > 0)
        {
            await _runtime.Journal.AppendAsync(records, cancellationToken).ConfigureAwait(false);
        }

        if (skipped > 0)
        {
            _logger.LogInformation(
                "AureliaSync: reconciliation skipped {Skipped} unchanged item(s) without projecting them",
                skipped);
        }

        await WriteInventoryAsync(scope, inventory, seen, cancellationToken).ConfigureAwait(false);

        if (artistsChanged)
        {
            // A track names its artists but identifies them through the library's artist entities,
            // so an artist appearing or vanishing changes track payloads without touching a single
            // track. Nothing in a track's own timestamp reflects that, which is why the next pass
            // has to be told to look at all of them.
            await InvalidateTrackProjectionsAsync(scope, cancellationToken).ConfigureAwait(false);
            return records.Count;
        }

        // Taken from before the pass began, so anything saved while it ran is examined next time
        // rather than falling into the gap between the two.
        await WriteWatermarkAsync(scope, passStarted, cancellationToken).ConfigureAwait(false);
        return records.Count;
    }

    /// <summary>
    /// Keeps an untouched item's inventory entry without loading or projecting it.
    /// </summary>
    /// <remarks>
    /// The inventory is rewritten wholesale at the end of a pass, so an item that is not carried
    /// forward would look deleted and produce a spurious tombstone.
    /// </remarks>
    private static void CarryForward(
        Dictionary<string, InventoryRow> known,
        HashSet<string> seen,
        List<InventoryRow> inventory,
        string entityType,
        Guid id,
        ref int skipped)
    {
        var key = entityType + ":" + id.ToString("N");
        if (!known.TryGetValue(key, out var previous))
        {
            // Unknown to the inventory but unchanged by timestamp: it appeared while nobody was
            // comparing, so it still has to be examined. Leaving it out of `seen` does that.
            return;
        }

        seen.Add(key);
        inventory.Add(previous);
        skipped++;
    }

    /// <summary>
    /// When this user was last reconciled, or null when never.
    /// </summary>
    /// <remarks>
    /// Rewound by a minute. Jellyfin's timestamps and the pass's own clock need not agree exactly,
    /// and re-examining a few items is free where missing one is a change never delivered.
    /// </remarks>
    private DateTime? ReadWatermark(string scope)
    {
        using var connection = _runtime.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", WatermarkKey(scope));

        return command.ExecuteScalar() is string text
            && DateTime.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.AddMinutes(-1)
            : null;
    }

    private Task<int> WriteWatermarkAsync(string scope, DateTime value, CancellationToken cancellationToken) =>
        _runtime.Database.WriteAsync(
            (connection, transaction) => SyncDatabase.ExecuteWithParameters(
                connection,
                transaction,
                """
                INSERT INTO meta (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """,
                ("$key", WatermarkKey(scope)),
                ("$value", value.ToString("O", CultureInfo.InvariantCulture))),
            cancellationToken);

    private static string WatermarkKey(string scope) => "reconcile.watermark." + scope;

    /// <summary>
    /// Makes the next pass re-project every track for one user.
    /// </summary>
    /// <remarks>
    /// Two things independently suppress work on a track, and both have to go: the watermark stops
    /// it being enumerated at all, and its recorded revision stops it being projected once it is.
    /// </remarks>
    /// <param name="scope">Owning user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the next pass is unblocked.</returns>
    private Task InvalidateTrackProjectionsAsync(string scope, CancellationToken cancellationToken) =>
        _runtime.Database.WriteAsync(
            (connection, transaction) =>
            {
                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    "DELETE FROM meta WHERE key = $key;",
                    ("$key", WatermarkKey(scope)));

                SyncDatabase.ExecuteWithParameters(
                    connection,
                    transaction,
                    """
                    UPDATE inventory SET observed_revision = NULL
                     WHERE scope = $scope AND entity_type = $type;
                    """,
                    ("$scope", scope),
                    ("$type", WireEntityType.Track));
            },
            cancellationToken);

    /// <summary>
    /// Jellyfin's own last-saved timestamp, in ticks, or null when it has none.
    /// </summary>
    /// <remarks>
    /// Null is treated as unknown rather than unchanged, so an item without a timestamp is always
    /// fully projected.
    /// </remarks>
    private static long? Revision(BaseItem item) =>
        item.DateLastSaved == default ? null : item.DateLastSaved.Ticks;

    private static void Compare(
        List<JournalRecord> records,
        List<InventoryRow> inventory,
        HashSet<string> seen,
        Dictionary<string, InventoryRow> known,
        string scope,
        string entityType,
        Guid id,
        byte[] payload,
        long? revision = null)
    {
        if (!Changed(known, seen, scope, entityType, id, payload, inventory, revision))
        {
            return;
        }

        records.Add(new JournalRecord(
            scope,
            WireKind.ItemUpsert,
            entityType,
            id.ToString("N"),
            WireSchema.WireSchemaVersionMax,
            payload));
    }

    /// <summary>
    /// Records that an entity was seen, and reports whether its payload differs from last time.
    /// </summary>
    private static bool Changed(
        Dictionary<string, InventoryRow> known,
        HashSet<string> seen,
        string scope,
        string entityType,
        Guid id,
        byte[] payload,
        List<InventoryRow> inventory,
        long? revision = null)
    {
        var entityId = id.ToString("N");
        var key = entityType + ":" + entityId;
        seen.Add(key);

        var checksum = Convert.ToHexStringLower(SHA256.HashData(payload));
        inventory.Add(new InventoryRow(scope, entityType, entityId, checksum, revision));

        return !known.TryGetValue(key, out var previous)
            || !string.Equals(previous.Checksum, checksum, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decides whether an item can be skipped without projecting it at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expensive part of a pass is building each item's payload in order to hash it. When
    /// Jellyfin's own <c>DateLastSaved</c> has not moved since the last pass, the payload cannot
    /// have changed either, so the projection can be skipped entirely.
    /// </para>
    /// <para>
    /// Both null cases mean <b>unknown</b> and fall through to full projection: a row written before
    /// revisions were recorded, and an item Jellyfin reports no timestamp for. Treating unknown as
    /// unchanged would silently stop reporting drift for exactly the items least well understood.
    /// </para>
    /// </remarks>
    private static bool CanSkip(
        Dictionary<string, InventoryRow> known,
        HashSet<string> seen,
        List<InventoryRow> inventory,
        string entityType,
        Guid id,
        long? revision)
    {
        if (revision is null)
        {
            return false;
        }

        var entityId = id.ToString("N");
        var key = entityType + ":" + entityId;

        if (!known.TryGetValue(key, out var previous) || previous.Revision != revision)
        {
            return false;
        }

        // Carried forward unchanged, so the row survives the wholesale rewrite at the end.
        seen.Add(key);
        inventory.Add(previous);
        return true;
    }

    private Dictionary<string, InventoryRow> ReadInventory(string scope)
    {
        var known = new Dictionary<string, InventoryRow>(StringComparer.Ordinal);

        using var connection = _runtime.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entity_type, entity_id, payload_checksum, observed_revision
              FROM inventory WHERE scope = $scope;
            """;
        command.Parameters.AddWithValue("$scope", scope);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var entityType = reader.GetString(0);
            var entityId = reader.GetString(1);
            known[entityType + ":" + entityId] = new InventoryRow(
                scope,
                entityType,
                entityId,
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3));
        }

        return known;
    }

    private Task WriteInventoryAsync(
        string scope, List<InventoryRow> rows, HashSet<string> seen, CancellationToken cancellationToken) =>
        _runtime.Database.WriteAsync(
            (connection, transaction) =>
            {
                // Replaced wholesale rather than merged: the pass just enumerated everything the
                // user can see, so anything absent is genuinely gone.
                SyncDatabase.ExecuteWithParameters(
                    connection, transaction, "DELETE FROM inventory WHERE scope = $scope;", ("$scope", scope));

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO inventory (scope, entity_type, entity_id, observed_revision,
                                           payload_checksum, last_seen_reconciliation)
                    VALUES ($scope, $type, $id, $revision, $checksum, $now);
                    """;

                var scopeParameter = command.Parameters.Add("$scope", SqliteType.Text);
                var type = command.Parameters.Add("$type", SqliteType.Text);
                var id = command.Parameters.Add("$id", SqliteType.Text);
                var checksum = command.Parameters.Add("$checksum", SqliteType.Text);
                var revision = command.Parameters.Add("$revision", SqliteType.Integer);
                var now = command.Parameters.Add("$now", SqliteType.Integer);

                scopeParameter.Value = scope;
                now.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                command.Prepare();

                foreach (var row in rows)
                {
                    type.Value = row.EntityType;
                    id.Value = row.EntityId;
                    checksum.Value = row.Checksum;
                    revision.Value = (object?)row.Revision ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }
            },
            cancellationToken);

    /// <summary>
    /// What the last pass recorded for one entity.
    /// </summary>
    /// <param name="Scope">Owning user.</param>
    /// <param name="EntityType">Wire entity type.</param>
    /// <param name="EntityId">Entity identifier.</param>
    /// <param name="Checksum">Hash of the payload the client was last told about.</param>
    /// <param name="Revision">
    /// Jellyfin's <c>DateLastSaved</c> in ticks, or null when unknown. Null means <b>unknown</b>,
    /// never <b>unchanged</b>: rows written before revisions were recorded, and items Jellyfin
    /// reports no timestamp for, must be fully projected rather than skipped.
    /// </param>
    private sealed record InventoryRow(
        string Scope, string EntityType, string EntityId, string Checksum, long? Revision);
}
