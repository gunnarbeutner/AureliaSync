using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AureliaSync.Streaming;
using Jellyfin.Plugin.AureliaSync.Wire;
using Jellyfin.Plugin.AureliaSync.Wire.Payloads;

namespace Jellyfin.Plugin.AureliaSync.Tests;

/// <summary>
/// Builds the conformance fixtures published under <c>docs/fixtures</c>.
/// </summary>
/// <remarks>
/// The fixtures are generated from the real wire types rather than hand-written, so they cannot
/// drift from the implementation. <see cref="FixtureTests"/> regenerates them and fails if the
/// committed files differ, which makes any change to the wire format a visible diff in a file the
/// client agent is reading.
/// </remarks>
internal static class FixtureBuilder
{
    private const long Generation = 17;

    private static readonly DateTimeOffset ServerTime =
        new DateTimeOffset(2026, 8, 13, 15, 48, 55, 123, TimeSpan.Zero);

    // Deliberately readable identifiers: a human comparing a fixture against a client decoder
    // should be able to see at a glance which record is which.
    private const string GenreId = "9e11a0000000000000000000000000g1";
    private const string ArtistId = "11111111111111111111111111111111";
    private const string AlbumId = "33333333333333333333333333333333";
    private const string TrackOneId = "44444444444444444444444444444444";
    private const string TrackTwoId = "55555555555555555555555555555555";
    private const string PlaylistId = "99999999999999999999999999999999";

    /// <summary>Names of the fixtures and the content each should hold.</summary>
    public static IReadOnlyDictionary<string, string> All() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["snapshot-complete.ndjson"] = CompleteSnapshot(),
            ["snapshot-partial.ndjson"] = PartialSegment(),
            ["stream-error.ndjson"] = ErroredSegment(),
            ["changes-segment.ndjson"] = ChangesSegment(),
            ["repair-segment.ndjson"] = RepairSegment()
        };

    /// <summary>
    /// A repair session: changed items, plus the manifest of what still exists.
    /// </summary>
    /// <remarks>
    /// The dangerous part of a repair is the manifest, so this fixture is built to make a wrong
    /// implementation fail rather than a right one pass. The manifest arrives in more than one chunk
    /// and lists identifiers this segment never upserts: a client that prunes per record, instead of
    /// accumulating every chunk and pruning once at promotion, deletes almost everything.
    /// </remarks>
    private static string RepairSegment()
    {
        var ordinal = 8100L;
        string Next() => Cursor.ForSnapshot(Generation, ++ordinal).Encode();

        var lines = new List<string>
        {
            Serialize(new SegmentBegin
            {
                SessionId = "kQ7xample000000000000000000000000000000000",
                Mode = "repair",
                Generation = Generation,
                ServerTime = ServerTime
            })
        };

        // Only the track that actually changed is re-sent. Everything else the client keeps.
        lines.Add(Record(Next(), ordinal, WireKind.ItemUpsert, WireEntityType.Track, TrackTwoId, new TrackPayload
        {
            Id = TrackTwoId,
            Name = "Jóga",
            AlbumId = AlbumId,
            AlbumName = "Homogenic",
            ArtistId = ArtistId,
            ArtistIDs = new[] { ArtistId },
            ArtistName = "Björk",
            GenreIDs = new[] { GenreId },
            IndexNumber = 2,
            Duration = 302.5,
            SortName = "0001 - 0002 - Joga"
        }));

        // Two chunks for one entity type, and TrackOneId appears only here — never upserted in this
        // segment. Pruning against either chunk alone would delete a track that must survive.
        lines.Add(Record(Next(), ordinal, WireKind.CatalogManifest, WireEntityType.Track, "manifest-0",
            new ManifestPayload
            {
                Id = "manifest-0",
                EntityType = WireEntityType.Track,
                Ids = new[] { TrackOneId }
            }));

        lines.Add(Record(Next(), ordinal, WireKind.CatalogManifest, WireEntityType.Track, "manifest-1",
            new ManifestPayload
            {
                Id = "manifest-1",
                EntityType = WireEntityType.Track,
                Ids = new[] { TrackTwoId }
            }));

        lines.Add(Record(Next(), ordinal, WireKind.CatalogManifest, WireEntityType.Album, "manifest-2",
            new ManifestPayload
            {
                Id = "manifest-2",
                EntityType = WireEntityType.Album,
                Ids = new[] { AlbumId }
            }));

        lines.Add(Serialize(new SegmentEnd
        {
            Cursor = Cursor.ForSnapshot(Generation, ordinal).Encode(),
            RecordCount = lines.Count - 1,
            ByteCount = 0,
            CaughtUp = true,
            SessionUpperBound = ordinal,
            StopReason = "upperBound",
            NextAfter = Cursor.ForSnapshot(Generation, ordinal).Encode()
        }));

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// A change session: journal cursors, a tombstone, and a user-data clear.
    /// </summary>
    private static string ChangesSegment()
    {
        var lines = new List<string>();
        var sequence = 4200L;

        string Next() => new Cursor(Cursor.JournalKind, 0, ++sequence).Encode();

        lines.Add(Serialize(new SegmentBegin
        {
            SessionId = "kQ7xample000000000000000000000000000000000",
            Mode = "changes",
            Generation = Generation,
            AfterCursor = new Cursor(Cursor.JournalKind, 0, sequence).Encode(),
            ServerTime = ServerTime
        }));

        // An ordinary edit: the same shape a snapshot would carry for this track.
        lines.Add(Record(Next(), sequence, WireKind.ItemUpsert, WireEntityType.Track, TrackOneId, new TrackPayload
        {
            Id = TrackOneId,
            Name = "Hunter (2026 remaster)",
            ArtistName = "Björk",
            ArtistId = ArtistId,
            ArtistIDs = new[] { ArtistId },
            AlbumName = "Homogenic",
            AlbumId = AlbumId,
            Duration = 244.28,
            IndexNumber = 1,
            ParentIndexNumber = 1,
            AlbumImageTag = "albumtag"
        }));

        // A tombstone. Only ever sent in changes mode; the payload carries nothing but the id,
        // because by now there is nothing left to describe.
        lines.Add(Record(Next(), sequence, WireKind.ItemDelete, WireEntityType.Track, TrackTwoId,
            new DeletedPayload { Id = TrackTwoId }));

        // Clearing user data states the cleared values explicitly: the client applies these with
        // COALESCE, so omitting them would leave the previous values in place.
        lines.Add(Record(Next(), sequence, WireKind.UserDataUpsert, null, TrackOneId, new UserDataPayload
        {
            Id = TrackOneId,
            IsFavorite = false,
            PlayCount = 0,
            PlaybackPositionTicks = 0
        }));

        lines.Add(Serialize(new SegmentEnd
        {
            Cursor = new Cursor(Cursor.JournalKind, 0, sequence).Encode(),
            RecordCount = lines.Count - 1,
            ByteCount = 0,
            CaughtUp = true,
            SessionUpperBound = sequence,
            StopReason = "upperBound",
            NextAfter = new Cursor(Cursor.JournalKind, 0, sequence).Encode()
        }));

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The payload of a tombstone: the identifier and nothing else.</summary>
    private sealed class DeletedPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>
    /// A whole tiny library delivered in one segment, ending with <c>caughtUp: true</c>.
    /// </summary>
    private static string CompleteSnapshot()
    {
        var lines = new List<string>();
        var ordinal = 0L;

        string Next() => Cursor.ForSnapshot(Generation, ++ordinal).Encode();

        lines.Add(Serialize(new SegmentBegin
        {
            SessionId = "kQ7xample000000000000000000000000000000000",
            Generation = Generation,
            AfterCursor = null,
            ServerTime = ServerTime
        }));

        // Ordering is genre, artist, album, track, playlist, playlist entries, user data: parents
        // before children, so a client applying incrementally never has a dangling reference.
        lines.Add(Record(Next(), ordinal, WireKind.ItemUpsert, WireEntityType.Genre, GenreId, new GenrePayload
        {
            Id = GenreId, Name = "Jazz", AlbumCount = 1
        }));

        lines.Add(Record(Next(), ordinal, WireKind.ItemUpsert, WireEntityType.Artist, ArtistId, new ArtistPayload
        {
            Id = ArtistId,
            Name = "Björk",
            Biography = "An artist with a non-ASCII name, to prove UTF-8 survives the wire.",
            AlbumCount = 1,
            ImageTag = "artisttag",
            IsAlbumArtist = true,
            IsFavorite = true
        }));

        lines.Add(Record(Next(), ordinal, WireKind.ItemUpsert, WireEntityType.Album, AlbumId, new AlbumPayload
        {
            Id = AlbumId,
            Name = "Homogenic",
            ArtistName = "Björk",
            ArtistId = ArtistId,
            ProductionYear = 1997,
            TrackCount = 2,
            GenreIDs = new[] { GenreId },
            ImageTag = "albumtag",
            IsFavorite = false
        }));

        lines.Add(Record(Next(), ordinal, WireKind.ItemUpsert, WireEntityType.Track, TrackOneId, new TrackPayload
        {
            Id = TrackOneId,
            Name = "Hunter",
            ArtistName = "Björk",
            ArtistId = ArtistId,
            ArtistIDs = new[] { ArtistId },
            AlbumName = "Homogenic",
            AlbumId = AlbumId,
            Duration = 244.28,
            IndexNumber = 1,
            ParentIndexNumber = 1,
            ProductionYear = 1997,
            GenreIDs = new[] { GenreId },
            AlbumImageTag = "albumtag",
            IsFavorite = true
        }));

        lines.Add(Record(Next(), ordinal, WireKind.ItemUpsert, WireEntityType.Track, TrackTwoId, new TrackPayload
        {
            Id = TrackTwoId,
            Name = "Jóga",
            ArtistName = "Björk",
            ArtistId = ArtistId,
            ArtistIDs = new[] { ArtistId },
            AlbumName = "Homogenic",
            AlbumId = AlbumId,
            Duration = 291.6,
            IndexNumber = 2,
            ParentIndexNumber = 1,
            ProductionYear = 1997,
            GenreIDs = new[] { GenreId },
            AlbumImageTag = "albumtag"
        }));

        lines.Add(Record(Next(), ordinal, WireKind.ItemUpsert, WireEntityType.Playlist, PlaylistId, new PlaylistPayload
        {
            Id = PlaylistId,
            Name = "Evening",
            TrackCount = 2,
            DateCreated = new DateTimeOffset(2024, 3, 1, 9, 30, 0, 0, TimeSpan.Zero)
        }));

        // One record per entry, each carrying the whole track. Every entry for a playlist is in the
        // same segment: the client deletes the playlist's membership and reinserts only what this
        // segment contains.
        lines.Add(Record(Next(), ordinal, WireKind.PlaylistReplace, null, TrackTwoId, new PlaylistEntryPayload
        {
            Id = TrackTwoId,
            Name = "Jóga",
            ArtistName = "Björk",
            ArtistId = ArtistId,
            ArtistIDs = new[] { ArtistId },
            AlbumName = "Homogenic",
            AlbumId = AlbumId,
            Duration = 291.6,
            IndexNumber = 2,
            ParentIndexNumber = 1,
            AlbumImageTag = "albumtag",
            PlaylistID = PlaylistId,
            PlaylistEntryID = "entry-a",
            Position = 0
        }));

        lines.Add(Record(Next(), ordinal, WireKind.PlaylistReplace, null, TrackOneId, new PlaylistEntryPayload
        {
            Id = TrackOneId,
            Name = "Hunter",
            ArtistName = "Björk",
            ArtistId = ArtistId,
            ArtistIDs = new[] { ArtistId },
            AlbumName = "Homogenic",
            AlbumId = AlbumId,
            Duration = 244.28,
            IndexNumber = 1,
            ParentIndexNumber = 1,
            AlbumImageTag = "albumtag",
            PlaylistID = PlaylistId,
            PlaylistEntryID = "entry-b",
            Position = 1
        }));

        // User data comes last, so favourites land on rows that already exist.
        lines.Add(Record(Next(), ordinal, WireKind.UserDataUpsert, null, TrackOneId, new UserDataPayload
        {
            Id = TrackOneId,
            IsFavorite = true,
            PlayCount = 12,
            LastPlayedAt = new DateTimeOffset(2026, 8, 12, 20, 15, 0, 0, TimeSpan.Zero),
            PlaybackPositionTicks = 0
        }));

        lines.Add(Record(Next(), ordinal, WireKind.UserDataUpsert, null, TrackTwoId, new UserDataPayload
        {
            Id = TrackTwoId,
            IsFavorite = false,
            PlayCount = 0,
            PlaybackPositionTicks = 1_200_000_000
        }));

        lines.Add(Serialize(new SegmentEnd
        {
            Cursor = Cursor.ForSnapshot(Generation, ordinal).Encode(),
            RecordCount = lines.Count - 1,
            ByteCount = 0,
            CaughtUp = true,
            SessionUpperBound = ordinal,
            StopReason = "upperBound",
            NextAfter = Cursor.ForSnapshot(Generation, ordinal).Encode()
        }));

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// A non-final segment: same framing, but <c>caughtUp: false</c>, so the client must ask again
    /// and must not promote its staged catalog.
    /// </summary>
    private static string PartialSegment()
    {
        var after = Cursor.ForSnapshot(Generation, 4200).Encode();
        var cursor = Cursor.ForSnapshot(Generation, 4201).Encode();

        var lines = new List<string>
        {
            Serialize(new SegmentBegin
            {
                SessionId = "kQ7xample000000000000000000000000000000000",
                Generation = Generation,
                AfterCursor = after,
                ServerTime = ServerTime
            }),
            Record(cursor, 4201, WireKind.ItemUpsert, WireEntityType.Track, TrackOneId, new TrackPayload
            {
                Id = TrackOneId,
                Name = "Hunter",
                AlbumId = AlbumId,
                Duration = 244.28
            }),
            Serialize(new SegmentEnd
            {
                Cursor = cursor,
                RecordCount = 1,
                ByteCount = 0,
                CaughtUp = false,
                SessionUpperBound = 34512,
                StopReason = "maxRecords",
                NextAfter = cursor
            })
        };

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// A segment that fails after the body has started. There is no <c>segment.end</c>, so the
    /// whole segment must be discarded and retried from the last acknowledged cursor.
    /// </summary>
    private static string ErroredSegment()
    {
        var cursor = Cursor.ForSnapshot(Generation, 4201).Encode();

        var lines = new List<string>
        {
            Serialize(new SegmentBegin
            {
                SessionId = "kQ7xample000000000000000000000000000000000",
                Generation = Generation,
                AfterCursor = Cursor.ForSnapshot(Generation, 4200).Encode(),
                ServerTime = ServerTime
            }),
            Record(cursor, 4201, WireKind.ItemUpsert, WireEntityType.Track, TrackOneId, new TrackPayload
            {
                Id = TrackOneId,
                Name = "Hunter",
                AlbumId = AlbumId,
                Duration = 244.28
            }),
            Serialize(new ErrorLine
            {
                Code = "snapshotInvalidated",
                Message = "The snapshot was rebuilt while this segment was being written.",
                CorrelationId = "8d2f0c1e4a5b4c6d8e9f0a1b2c3d4e5f"
            })
        };

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// Payload bytes of the records emitted so far, in order, so a fixture can carry the same
    /// aggregate digest the segment writer would produce for it.
    /// </summary>
    private static string Record(
        string cursor, long sequence, string kind, string? entityType, string entityId, object payload)
    {

        return Serialize(new RecordEnvelope
        {
            Cursor = cursor,
            Sequence = sequence,
            Kind = kind,
            EntityType = entityType,
            EntityId = entityId,
            Payload = payload
        });
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, WireSchema.JsonOptions);

    /// <summary>Locates the repository's <c>docs/fixtures</c> directory from the test binary.</summary>
    public static string FixtureDirectory()
    {
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && directory is not null; i++)
        {
            var candidate = System.IO.Path.Combine(directory, "docs", "fixtures");
            if (System.IO.Directory.Exists(System.IO.Path.Combine(directory, "docs")))
            {
                System.IO.Directory.CreateDirectory(candidate);
                return candidate;
            }

            directory = System.IO.Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException(
            string.Format(CultureInfo.InvariantCulture, "Could not locate docs/ above {0}", AppContext.BaseDirectory));
    }
}
