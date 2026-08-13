using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AureliaSync.Configuration;
using Jellyfin.Plugin.AureliaSync.Projection;
using Jellyfin.Plugin.AureliaSync.Storage;
using Jellyfin.Plugin.AureliaSync.Wire;
using Jellyfin.Plugin.AureliaSync.Wire.Payloads;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AureliaSync.Snapshots;

/// <summary>
/// Materialises one user's visible music library into a snapshot.
/// </summary>
/// <remarks>
/// <para>
/// <b>Records are written in a different order from the one they are numbered in.</b> Ordinal
/// ranges are reserved up front from the enumeration counts, then each phase writes into its own
/// range. This is what resolves an otherwise circular dependency: albums must be numbered before
/// their tracks, but an album's track count is only known once its tracks have been counted. Since
/// a snapshot is only ever streamed after it completes, the order rows are inserted in is
/// invisible to clients.
/// </para>
/// <para>
/// Reserved ranges may end up sparse — if hydration drops an item the enumeration listed, its
/// ordinal simply goes unused. Gaps are harmless: delivery reads <c>ordinal &gt; after</c> in
/// order, and cursors are opaque.
/// </para>
/// <para>
/// No Jellyfin transaction is held. Every call is a bounded, independent query, and writes go into
/// the plugin's own database in batches, so a build is a finite background task rather than
/// something the rest of the server has to wait behind.
/// </para>
/// </remarks>
public sealed class SnapshotBuilder
{
    private readonly IUserManager _userManager;
    private readonly SnapshotStore _store;
    private readonly LibraryEnumerator _enumerator;
    private readonly BaseItemFactsReader _reader;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotBuilder"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="imageProcessor">Used to compute image cache tags.</param>
    /// <param name="store">Where snapshots are persisted.</param>
    /// <param name="logger">Logger.</param>
    public SnapshotBuilder(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IImageProcessor imageProcessor,
        SnapshotStore store,
        ILogger logger)
    {
        _userManager = userManager;
        _store = store;
        _logger = logger;
        _enumerator = new LibraryEnumerator(libraryManager, logger);
        _reader = new BaseItemFactsReader(imageProcessor, logger);
    }

    /// <summary>
    /// Builds a snapshot for one user.
    /// </summary>
    /// <param name="userId">The user whose visible library to capture.</param>
    /// <param name="generation">The snapshot generation to fill, already created.</param>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of records written.</returns>
    public async Task<long> BuildAsync(
        Guid userId,
        long generation,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var user = _userManager.GetUserById(userId)
            ?? throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "Unknown user {0}", userId));

        var batchSize = Math.Max(50, configuration.SnapshotHydrationBatchSize);
        var started = DateTimeOffset.UtcNow;

        // ---- Enumerate. Every one of these is access-filtered; see LibraryEnumerator. ----
        var genres = _enumerator.Genres(user);
        var artists = _enumerator.Artists(user);
        var albumArtistIds = _enumerator.AlbumArtistIds(user);
        var albumIds = _enumerator.EnumerateIds(user, BaseItemKind.MusicAlbum);
        var trackIds = _enumerator.EnumerateIds(user, BaseItemKind.Audio);
        var playlists = _enumerator.Playlists(user, configuration.AudioPlaylistsOnly);

        _logger.LogInformation(
            "AureliaSync: snapshot {Generation} covers {Genres} genres, {Artists} artists, {Albums} albums, {Tracks} tracks, {Playlists} playlists",
            generation,
            genres.Count,
            artists.Count,
            albumIds.Count,
            trackIds.Count,
            playlists.Count);

        // ---- Reserve one ordinal range per phase, in the order rows can be produced. ----
        var ordinals = new OrdinalRanges(genres.Count, artists.Count, albumIds.Count, trackIds.Count);

        var artistIdsByName = LibraryEnumerator.ArtistIdsByName(artists);
        var albumSummaries = new Dictionary<Guid, AlbumSummary>();
        var projector = new PayloadProjector(artistIdsByName, albumSummaries, _enumerator.GenreId);

        var userData = new List<UserDataPayload>();
        long written = 0;

        // ---- Albums are hydrated first, because tracks need their names and artwork. ----
        await _store.SetProgressAsync(generation, "album", 0, albumIds.Count, cancellationToken).ConfigureAwait(false);
        var albumFacts = new List<ItemFacts>(albumIds.Count);

        // Album counts are derived here rather than taken from Jellyfin's ItemCounts, which is only
        // populated when a query explicitly asks for counts and is otherwise null. Deriving them
        // also makes the numbers agree with what is actually sent.
        var albumCountByArtist = new Dictionary<Guid, int>();
        var albumCountByGenre = new Dictionary<Guid, int>();

        foreach (var batch in LibraryEnumerator.Batch(albumIds, batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var item in _enumerator.Hydrate(user, batch))
            {
                var facts = _reader.Read(item, userId);
                albumFacts.Add(facts);
                albumSummaries[facts.Id] = new AlbumSummary(facts.Name, facts.ImageTag);
                Collect(projector, facts, userData);

                var credits = facts.AlbumArtistNames.Count > 0 ? facts.AlbumArtistNames : facts.ArtistNames;
                foreach (var artistId in projector.ResolveArtists(credits))
                {
                    if (Guid.TryParseExact(artistId, "N", out var parsed))
                    {
                        albumCountByArtist[parsed] = albumCountByArtist.GetValueOrDefault(parsed) + 1;
                    }
                }

                foreach (var genreId in projector.ResolveGenres(facts.GenreNames) ?? Enumerable.Empty<string>())
                {
                    if (Guid.TryParseExact(genreId, "N", out var parsed))
                    {
                        albumCountByGenre[parsed] = albumCountByGenre.GetValueOrDefault(parsed) + 1;
                    }
                }
            }

            await ThrottleAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        // ---- Tracks: written straight into their reserved range, counting per album as we go. ----
        await _store.SetProgressAsync(generation, "track", 0, trackIds.Count, cancellationToken).ConfigureAwait(false);
        var albumIdSet = albumIds.ToHashSet();
        var trackCountByAlbum = new Dictionary<Guid, int>();
        var trackFactsById = new Dictionary<Guid, ItemFacts>();
        var trackOrdinal = ordinals.TrackStart;
        long done = 0;

        foreach (var batch in LibraryEnumerator.Batch(trackIds, batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rows = new List<SnapshotRow>(batch.Count);

            foreach (var item in _enumerator.Hydrate(user, batch))
            {
                var facts = _reader.Read(item, userId, albumIdSet);
                var payload = projector.Track(facts);

                rows.Add(NewRow(++trackOrdinal, WireKind.ItemUpsert, WireEntityType.Track, payload.Id, payload));
                Collect(projector, facts, userData);

                if (facts.AlbumId is { } albumId)
                {
                    trackCountByAlbum[albumId] = trackCountByAlbum.GetValueOrDefault(albumId) + 1;
                }

                // Playlist entries repeat the whole track, and a playlist can reference a track
                // filed anywhere, so the facts are kept rather than re-read later.
                trackFactsById[facts.Id] = facts;
            }

            await _store.AppendAsync(generation, rows, cancellationToken).ConfigureAwait(false);
            written += rows.Count;
            done += batch.Count;

            // Published only after the batch is committed, so the watermark never advertises a row
            // that is still being written. This is what lets the client start applying the catalog
            // seconds into a build that takes minutes.
            await _store.SetStreamableThroughAsync(generation, trackOrdinal, cancellationToken)
                .ConfigureAwait(false);

            await _store.SetProgressAsync(generation, "track", done, trackIds.Count, cancellationToken)
                .ConfigureAwait(false);
            await ThrottleAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        // ---- Genres and artists: small, and independent of everything above. ----
        await _store.SetProgressAsync(generation, "genre", 0, genres.Count, cancellationToken)
            .ConfigureAwait(false);
        var genreRows = new List<SnapshotRow>(genres.Count);
        var genreOrdinal = ordinals.GenreStart;
        foreach (var (item, _) in genres.OrderBy(g => g.Item.Id.ToString("N"), StringComparer.Ordinal))
        {
            var payload = projector.Genre(
                item.Id, item.Name ?? string.Empty, albumCountByGenre.GetValueOrDefault(item.Id));
            genreRows.Add(NewRow(++genreOrdinal, WireKind.ItemUpsert, WireEntityType.Genre, payload.Id, payload));
        }

        await _store.AppendAsync(generation, genreRows, cancellationToken).ConfigureAwait(false);
        written += genreRows.Count;
        await _store.SetStreamableThroughAsync(generation, genreOrdinal, cancellationToken).ConfigureAwait(false);

        await _store.SetProgressAsync(generation, "artist", 0, artists.Count, cancellationToken)
            .ConfigureAwait(false);
        var artistRows = new List<SnapshotRow>(artists.Count);
        var artistOrdinal = ordinals.ArtistStart;
        foreach (var (item, _) in artists.OrderBy(a => a.Item.Id.ToString("N"), StringComparer.Ordinal))
        {
            var facts = _reader.Read(item, userId);
            var payload = projector.Artist(
                facts, albumCountByArtist.GetValueOrDefault(item.Id), albumArtistIds.Contains(item.Id));
            artistRows.Add(NewRow(++artistOrdinal, WireKind.ItemUpsert, WireEntityType.Artist, payload.Id, payload));
            Collect(projector, facts, userData);
        }

        await _store.AppendAsync(generation, artistRows, cancellationToken).ConfigureAwait(false);
        written += artistRows.Count;
        await _store.SetStreamableThroughAsync(generation, artistOrdinal, cancellationToken).ConfigureAwait(false);

        // ---- Albums, now that their track counts are known. ----
        await _store.SetProgressAsync(generation, "albumWrite", 0, albumFacts.Count, cancellationToken)
            .ConfigureAwait(false);
        var albumRows = new List<SnapshotRow>(albumFacts.Count);
        var albumOrdinal = ordinals.AlbumStart;
        foreach (var facts in albumFacts.OrderBy(a => a.Id.ToString("N"), StringComparer.Ordinal))
        {
            var payload = projector.Album(facts, trackCountByAlbum.GetValueOrDefault(facts.Id));
            albumRows.Add(NewRow(++albumOrdinal, WireKind.ItemUpsert, WireEntityType.Album, payload.Id, payload));
        }

        await _store.AppendAsync(generation, albumRows, cancellationToken).ConfigureAwait(false);
        written += albumRows.Count;
        await _store.SetStreamableThroughAsync(generation, albumOrdinal, cancellationToken).ConfigureAwait(false);

        // ---- Playlists and their membership. ----
        await _store.SetProgressAsync(generation, "playlist", 0, playlists.Count, cancellationToken)
            .ConfigureAwait(false);

        // Playlists, their membership and user data have no count known in advance, so they simply
        // continue from the end of the reserved ranges.
        var tailOrdinal = ordinals.TailStart;

        foreach (var playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entries = PlaylistMembershipReader.Read(
                playlist, user, userId, _reader, albumIdSet, trackFactsById);

            var groupKey = playlist.Id.ToString("N");
            var playlistFacts = _reader.Read(playlist, userId);
            var playlistPayload = projector.Playlist(playlistFacts, entries.Count);

            // The upsert leads its own entries and shares their group key, so the two never land in
            // different segments. The client treats a playlist upsert as "clear this playlist's
            // membership"; membership cleared in one segment and refilled in the next would leave
            // the playlist empty for as long as the client sat between them.
            var rows = new List<SnapshotRow>(entries.Count + 1)
            {
                NewRow(
                    ++tailOrdinal,
                    WireKind.ItemUpsert,
                    WireEntityType.Playlist,
                    playlistPayload.Id,
                    playlistPayload,
                    groupKey)
            };

            foreach (var (facts, entryId, position) in entries)
            {
                var payload = projector.PlaylistEntry(facts, playlist.Id, entryId, position);
                rows.Add(NewRow(++tailOrdinal, WireKind.PlaylistReplace, null, payload.Id, payload, groupKey));
            }

            await _store.AppendAsync(generation, rows, cancellationToken).ConfigureAwait(false);
            written += rows.Count;
            await _store.SetStreamableThroughAsync(generation, tailOrdinal, cancellationToken)
                .ConfigureAwait(false);

            Collect(projector, playlistFacts, userData);
        }

        // ---- User data last, so favourites land on rows that already exist. ----
        await _store.SetProgressAsync(generation, "userData", 0, userData.Count, cancellationToken)
            .ConfigureAwait(false);
        var userDataRows = new List<SnapshotRow>(userData.Count);
        foreach (var payload in userData.OrderBy(u => u.Id, StringComparer.Ordinal))
        {
            userDataRows.Add(NewRow(++tailOrdinal, WireKind.UserDataUpsert, null, payload.Id, payload));
        }

        await _store.AppendAsync(generation, userDataRows, cancellationToken).ConfigureAwait(false);
        written += userDataRows.Count;

        // The last rows only become deliverable once the snapshot is sealed, because until then a
        // client reaching the watermark cannot tell "nothing more yet" from "nothing more ever".
        await _store.SetStreamableThroughAsync(generation, tailOrdinal, cancellationToken)
            .ConfigureAwait(false);

        // ---- Seal it. ----
        var (checksum, bytes) = ComputeChecksum(generation);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(Math.Max(1, configuration.SnapshotRetentionHours));
        await _store.CompleteAsync(generation, written, bytes, checksum, expiresAt, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "AureliaSync: snapshot {Generation} complete — {Records} records, {Bytes} bytes, in {Elapsed}",
            generation,
            written,
            bytes,
            DateTimeOffset.UtcNow - started);

        return written;
    }

    private static void Collect(PayloadProjector projector, ItemFacts facts, List<UserDataPayload> sink)
    {
        var payload = projector.UserData(facts);
        if (payload is not null)
        {
            sink.Add(payload);
        }
    }

    private static SnapshotRow NewRow<T>(
        long ordinal, string kind, string? entityType, string entityId, T payload, string? groupKey = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, WireSchema.JsonOptions);
        return new SnapshotRow(ordinal, kind, entityType, entityId, bytes, null, groupKey);
    }

    private static Task ThrottleAsync(PluginConfiguration configuration, CancellationToken cancellationToken) =>
        configuration.SnapshotBatchDelayMs > 0
            ? Task.Delay(configuration.SnapshotBatchDelayMs, cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Hashes the snapshot's payloads in ordinal order.
    /// </summary>
    /// <remarks>
    /// Computed by reading back what was actually stored rather than by accumulating during the
    /// build, both because rows are written out of ordinal order and because this way the checksum
    /// describes the snapshot on disk rather than the intent that produced it.
    /// </remarks>
    private (string Checksum, long Bytes) ComputeChecksum(long generation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long ordinal = 0;
        long bytes = 0;

        while (true)
        {
            var page = _store.ReadAfter(generation, ordinal, 2_000, long.MaxValue);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var row in page)
            {
                hash.AppendData(row.Payload);
                bytes += row.Payload.Length;
            }

            ordinal = page[^1].Ordinal;
        }

        return ("sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()), bytes);
    }

    /// <summary>
    /// The ordinal range reserved for each phase.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order here is the wire order, and it exists to match the order rows can actually be
    /// produced.</b> Only tracks have no aggregate dependency, so they go first; albums need every
    /// track counted, and artists and genres need every album read. Reserving ranges in any other
    /// order means the low ordinals are filled in last, the deliverable prefix stays empty until the
    /// build is nearly done, and the client waits out the whole build receiving nothing.
    /// </para>
    /// <para>
    /// Playlists, their membership and user data have no known count in advance, so they are not
    /// reserved at all — they continue sequentially from <see cref="TailStart"/>, which keeps the
    /// whole build strictly ascending and the watermark meaningful.
    /// </para>
    /// </remarks>
    private readonly struct OrdinalRanges
    {
        public OrdinalRanges(int genres, int artists, int albums, int tracks)
        {
            TrackStart = 0;
            GenreStart = TrackStart + tracks;
            ArtistStart = GenreStart + genres;
            AlbumStart = ArtistStart + artists;
            TailStart = AlbumStart + albums;
        }

        public long TrackStart { get; }

        public long GenreStart { get; }

        public long ArtistStart { get; }

        public long AlbumStart { get; }

        public long TailStart { get; }
    }
}
