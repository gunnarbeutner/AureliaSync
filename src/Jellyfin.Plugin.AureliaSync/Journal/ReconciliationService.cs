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
    private readonly ILibraryManager _libraryManager;
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
        _libraryManager = libraryManager;
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<JournalRecord>();
        var inventory = new List<InventoryRow>();

        var albumIds = _enumerator.EnumerateIds(user, BaseItemKind.MusicAlbum);
        var albumIdSet = albumIds.ToHashSet();
        var albumSummaries = new Dictionary<Guid, AlbumSummary>();
        var artists = _enumerator.Artists(user);
        var projector = new PayloadProjector(
            LibraryEnumerator.ArtistIdsByName(artists), albumSummaries, _enumerator.GenreId);

        var batchSize = Math.Max(50, configuration.SnapshotHydrationBatchSize);

        // Albums first, so tracks can name their album without a second pass.
        foreach (var batch in LibraryEnumerator.Batch(albumIds, batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in _enumerator.Hydrate(user, batch))
            {
                var facts = _reader.Read(item, user.Id);
                albumSummaries[facts.Id] = new AlbumSummary(facts.Name, facts.ImageTag);

                Compare(
                    records,
                    inventory,
                    seen,
                    known,
                    scope,
                    WireEntityType.Album,
                    facts.Id,
                    JsonSerializer.SerializeToUtf8Bytes(projector.Album(facts, null), WireSchema.JsonOptions));
            }
        }

        var trackIds = _enumerator.EnumerateIds(user, BaseItemKind.Audio);
        foreach (var batch in LibraryEnumerator.Batch(trackIds, batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in _enumerator.Hydrate(user, batch))
            {
                var facts = _reader.Read(item, user.Id, albumIdSet);

                Compare(
                    records,
                    inventory,
                    seen,
                    known,
                    scope,
                    WireEntityType.Track,
                    facts.Id,
                    JsonSerializer.SerializeToUtf8Bytes(projector.Track(facts), WireSchema.JsonOptions));
            }
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
                    projector.Playlist(facts, entries.Count), WireSchema.JsonOptions)));

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

        if (records.Count > 0)
        {
            await _runtime.Journal.AppendAsync(records, cancellationToken).ConfigureAwait(false);
        }

        await WriteInventoryAsync(scope, inventory, seen, cancellationToken).ConfigureAwait(false);
        return records.Count;
    }

    private static void Compare(
        List<JournalRecord> records,
        List<InventoryRow> inventory,
        HashSet<string> seen,
        Dictionary<string, InventoryRow> known,
        string scope,
        string entityType,
        Guid id,
        byte[] payload)
    {
        if (!Changed(known, seen, scope, entityType, id, payload, inventory))
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
        List<InventoryRow> inventory)
    {
        var entityId = id.ToString("N");
        var key = entityType + ":" + entityId;
        seen.Add(key);

        var checksum = Convert.ToHexStringLower(SHA256.HashData(payload));
        inventory.Add(new InventoryRow(scope, entityType, entityId, checksum));

        return !known.TryGetValue(key, out var previous)
            || !string.Equals(previous.Checksum, checksum, StringComparison.Ordinal);
    }

    private Dictionary<string, InventoryRow> ReadInventory(string scope)
    {
        var known = new Dictionary<string, InventoryRow>(StringComparer.Ordinal);

        using var connection = _runtime.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entity_type, entity_id, payload_checksum FROM inventory WHERE scope = $scope;
            """;
        command.Parameters.AddWithValue("$scope", scope);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var entityType = reader.GetString(0);
            var entityId = reader.GetString(1);
            known[entityType + ":" + entityId] = new InventoryRow(
                scope, entityType, entityId, reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
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
                    VALUES ($scope, $type, $id, NULL, $checksum, $now);
                    """;

                var scopeParameter = command.Parameters.Add("$scope", SqliteType.Text);
                var type = command.Parameters.Add("$type", SqliteType.Text);
                var id = command.Parameters.Add("$id", SqliteType.Text);
                var checksum = command.Parameters.Add("$checksum", SqliteType.Text);
                var now = command.Parameters.Add("$now", SqliteType.Integer);

                scopeParameter.Value = scope;
                now.Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                command.Prepare();

                foreach (var row in rows)
                {
                    type.Value = row.EntityType;
                    id.Value = row.EntityId;
                    checksum.Value = row.Checksum;
                    command.ExecuteNonQuery();
                }
            },
            cancellationToken);

    private sealed record InventoryRow(string Scope, string EntityType, string EntityId, string Checksum);
}
