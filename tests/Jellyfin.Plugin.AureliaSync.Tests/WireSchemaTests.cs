using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AureliaSync.Wire;
using Jellyfin.Plugin.AureliaSync.Wire.Payloads;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

/// <summary>
/// Golden-file tests for the wire format.
/// </summary>
/// <remarks>
/// The Aurelia client decodes with <c>.useDefaultKeys</c>, so a key that differs by one capital
/// letter silently decodes as nil rather than failing loudly. These assertions on exact serialised
/// text are the only thing standing between a renamed property and a field that quietly stops
/// arriving. Changing an expectation here means changing the protocol — update
/// <c>docs/PROTOCOL.md</c> and tell the client agent.
/// </remarks>
public class WireSchemaTests
{
    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, WireSchema.JsonOptions);

    [Fact]
    public void TrackSerialisesWithExactlyTheAgreedKeys()
    {
        var json = Serialize(new TrackPayload
        {
            Id = "6f3a1b2c3d4e5f60718293a4b5c6d7e8",
            Name = "Song",
            SortName = "Song",
            ArtistName = "A & B",
            ArtistId = "11111111111111111111111111111111",
            ArtistIDs = new[] { "11111111111111111111111111111111", "22222222222222222222222222222222" },
            AlbumName = "Record",
            AlbumId = "33333333333333333333333333333333",
            Duration = 213.5,
            IndexNumber = 4,
            ParentIndexNumber = 1,
            ProductionYear = 1997,
            GenreIDs = new[] { "44444444444444444444444444444444" },
            ImageTag = "tagA",
            AlbumImageTag = "tagB",
            IsFavorite = true
        });

        Assert.Equal(
            "{\"id\":\"6f3a1b2c3d4e5f60718293a4b5c6d7e8\",\"name\":\"Song\",\"sortName\":\"Song\","
            + "\"artistName\":\"A & B\",\"artistId\":\"11111111111111111111111111111111\","
            + "\"artistIDs\":[\"11111111111111111111111111111111\",\"22222222222222222222222222222222\"],"
            + "\"albumName\":\"Record\",\"albumId\":\"33333333333333333333333333333333\",\"duration\":213.5,"
            + "\"indexNumber\":4,\"parentIndexNumber\":1,\"productionYear\":1997,"
            + "\"genreIDs\":[\"44444444444444444444444444444444\"],\"imageTag\":\"tagA\","
            + "\"albumImageTag\":\"tagB\",\"isFavorite\":true}",
            json);
    }

    [Fact]
    public void AlbumArtistPlaylistAndGenreUseTheAgreedKeys()
    {
        Assert.Equal(
            "{\"id\":\"a1\",\"name\":\"Rec\",\"artistName\":\"A\",\"artistId\":\"b1\","
            + "\"productionYear\":2001,\"trackCount\":9,\"genreIDs\":[\"g1\"],\"imageTag\":\"t\",\"isFavorite\":false}",
            Serialize(new AlbumPayload
            {
                Id = "a1", Name = "Rec", ArtistName = "A", ArtistId = "b1",
                ProductionYear = 2001, TrackCount = 9,
                GenreIDs = new[] { "g1" }, ImageTag = "t", IsFavorite = false
            }));

        Assert.Equal(
            "{\"id\":\"b1\",\"name\":\"A\",\"biography\":\"bio\",\"albumCount\":3,\"imageTag\":\"t\"}",
            Serialize(new ArtistPayload
            {
                Id = "b1", Name = "A", Biography = "bio", AlbumCount = 3, ImageTag = "t"
            }));

        Assert.Equal(
            "{\"id\":\"p1\",\"name\":\"Mix\",\"trackCount\":2,\"dateCreated\":\"2026-08-13T15:48:55.123Z\"}",
            Serialize(new PlaylistPayload
            {
                Id = "p1", Name = "Mix", TrackCount = 2,
                DateCreated = new DateTimeOffset(2026, 8, 13, 15, 48, 55, 123, TimeSpan.Zero)
            }));

        // Genres carry no artwork, sort name, or user state: the client has nowhere to put them.
        Assert.Equal(
            "{\"id\":\"g1\",\"name\":\"Jazz\",\"albumCount\":7}",
            Serialize(new GenrePayload { Id = "g1", Name = "Jazz", AlbumCount = 7 }));
    }

    [Fact]
    public void PlaylistEntryCarriesTheWholeTrackAlongsideItsMembership()
    {
        // The client inserts the membership row and upserts the track from this single record, so
        // a bare identifier would leave it with a playlist full of unknown tracks.
        var json = Serialize(new PlaylistEntryPayload
        {
            Id = "t1", Name = "Song", Duration = 100,
            PlaylistID = "p1", PlaylistEntryID = "e1", Position = 0
        });

        Assert.Contains("\"playlistID\":\"p1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"playlistEntryID\":\"e1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"position\":0", json, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"t1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"Song\"", json, StringComparison.Ordinal);
        Assert.Contains("\"duration\":100", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UserDataDistinguishesClearingFromOmitting()
    {
        // The client applies user data with COALESCE, so a cleared value must be sent explicitly;
        // omitting it leaves whatever was there before.
        Assert.Equal(
            "{\"id\":\"t1\",\"isFavorite\":false,\"playCount\":0,\"playbackPositionTicks\":0}",
            Serialize(new UserDataPayload
            {
                Id = "t1", IsFavorite = false, PlayCount = 0, PlaybackPositionTicks = 0
            }));

        Assert.Equal(
            "{\"id\":\"t1\"}",
            Serialize(new UserDataPayload { Id = "t1" }));
    }

    [Fact]
    public void NonAsciiNamesAreEmittedAsUtf8RatherThanEscaped()
    {
        // A library full of accented names would otherwise pay nearly double the bytes per name.
        var json = Serialize(new ArtistPayload { Id = "b1", Name = "Björk Guðmundsdóttir" });

        Assert.Equal("{\"id\":\"b1\",\"name\":\"Björk Guðmundsdóttir\"}", json);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CharactersJsonActuallyRequiresEscapingAreStillEscaped()
    {
        // The relaxed encoder is not "no escaping" — quotes, backslashes and control characters
        // must still be escaped or the stream stops being parseable line by line.
        var json = Serialize(new ArtistPayload { Id = "b1", Name = "He said \"hi\"\\ then\nleft" });

        Assert.Contains("\\\"hi\\\"", json, StringComparison.Ordinal);
        Assert.Contains("\\\\", json, StringComparison.Ordinal);
        Assert.Contains("\\n", json, StringComparison.Ordinal);

        // Critically: no raw newline may survive into a line-delimited format.
        Assert.DoesNotContain('\n', json);
    }

    [Fact]
    public void NullsAreOmittedRatherThanEmitted()
    {
        // Over 34,500 records, emitting nulls would cost megabytes.
        var json = Serialize(new ArtistPayload { Id = "b1", Name = "A" });

        Assert.Equal("{\"id\":\"b1\",\"name\":\"A\"}", json);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2026, 8, 13, 15, 48, 55, 123, "2026-08-13T15:48:55.123Z")]
    [InlineData(2026, 1, 2, 3, 4, 5, 0, "2026-01-02T03:04:05.000Z")]
    [InlineData(1999, 12, 31, 23, 59, 59, 999, "1999-12-31T23:59:59.999Z")]
    public void DatesUseExactlyThreeFractionalDigitsAndZ(
        int year, int month, int day, int hour, int minute, int second, int ms, string expected)
    {
        var value = new DateTimeOffset(year, month, day, hour, minute, second, ms, TimeSpan.Zero);

        Assert.Equal($"\"{expected}\"", Serialize(value));
    }

    [Fact]
    public void NonUtcDatesAreConvertedRatherThanEmittedWithAnOffset()
    {
        // ISO8601DateFormatter would accept an offset, but the whole protocol is UTC; normalising
        // here keeps the on-wire form to exactly one shape.
        var value = new DateTimeOffset(2026, 8, 13, 17, 48, 55, 123, TimeSpan.FromHours(2));

        Assert.Equal("\"2026-08-13T15:48:55.123Z\"", Serialize(value));
    }

    [Fact]
    public void EveryEmittedDateMatchesWhatTheClientCanParse()
    {
        // ISO8601DateFormatter with .withFractionalSeconds accepts exactly three fractional digits
        // or none. .NET's default round-trip format emits seven and fails to parse on the client,
        // which is the single most likely silent interop break in this protocol.
        var pattern = new Regex(@"^""\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z""$", RegexOptions.None);

        foreach (var value in new[]
                 {
                     DateTimeOffset.UtcNow,
                     DateTimeOffset.UnixEpoch,
                     new DateTimeOffset(2028, 2, 29, 0, 0, 0, 1, TimeSpan.Zero)
                 })
        {
            var json = Serialize(value);
            Assert.Matches(pattern, json);
            Assert.DoesNotContain("+00:00", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SegmentControlLinesCarryTheirDiscriminator()
    {
        var begin = Serialize(new SegmentBegin
        {
            SessionId = "s1",
            Generation = 17,
            ServerTime = new DateTimeOffset(2026, 8, 13, 15, 48, 55, 123, TimeSpan.Zero)
        });
        Assert.Contains("\"kind\":\"segment.begin\"", begin, StringComparison.Ordinal);
        Assert.Contains("\"wireSchemaVersion\":1", begin, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"snapshot\"", begin, StringComparison.Ordinal);

        var end = Serialize(new SegmentEnd
        {
            Cursor = "c1", RecordCount = 3, CaughtUp = true, StopReason = "upperBound"
        });
        Assert.Contains("\"kind\":\"segment.end\"", end, StringComparison.Ordinal);
        Assert.Contains("\"cursor\":\"c1\"", end, StringComparison.Ordinal);
        Assert.Contains("\"caughtUp\":true", end, StringComparison.Ordinal);

        var error = Serialize(new ErrorLine { Code = "snapshotInvalidated", Message = "m", CorrelationId = "x" });
        Assert.Contains("\"kind\":\"error\"", error, StringComparison.Ordinal);
    }

    [Fact]
    public void CaughtUpIsAlwaysPresentEvenWhenFalse()
    {
        // The client defaults a missing caughtUp to false, so omitting it would still work — but
        // only by accident. Being explicit keeps the terminating condition visible on the wire.
        Assert.Contains("\"caughtUp\":false", Serialize(new SegmentEnd { Cursor = "c1" }), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordKindsAreLimitedToWhatTheClientTolerates()
    {
        // An unrecognised kind aborts the client's sync rather than being skipped, so this list is
        // closed for protocol v1.
        var emittable = new HashSet<string>(StringComparer.Ordinal)
        {
            WireKind.ItemUpsert, WireKind.PlaylistReplace, WireKind.UserDataUpsert
        };

        var tolerated = new HashSet<string>(StringComparer.Ordinal)
        {
            WireKind.SegmentBegin, WireKind.SegmentEnd, WireKind.Error,
            WireKind.ItemDelete, WireKind.RelationshipReplace, WireKind.ControlReconcile
        };

        Assert.All(emittable, kind => Assert.DoesNotContain(kind, tolerated));
        Assert.Equal("item.upsert", WireKind.ItemUpsert);
        Assert.Equal("playlist.replace", WireKind.PlaylistReplace);
        Assert.Equal("userData.upsert", WireKind.UserDataUpsert);
        Assert.Equal("segment.begin", WireKind.SegmentBegin);
        Assert.Equal("segment.end", WireKind.SegmentEnd);
    }
}
