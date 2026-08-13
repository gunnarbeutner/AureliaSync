using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AureliaSync.Projection;
using Xunit;

namespace Jellyfin.Plugin.AureliaSync.Tests;

public class PayloadProjectorTests
{
    private static readonly Guid ArtistA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ArtistB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AlbumId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TrackId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static PayloadProjector NewProjector(
        IReadOnlyDictionary<Guid, AlbumSummary>? albums = null,
        IReadOnlyDictionary<string, Guid>? artists = null)
    {
        // Case-insensitive, matching how Jellyfin resolves artist names.
        var artistMap = artists ?? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["Alpha"] = ArtistA,
            ["Beta"] = ArtistB
        };

        var albumMap = albums ?? new Dictionary<Guid, AlbumSummary>
        {
            [AlbumId] = new AlbumSummary("Record", "albumtag")
        };

        // Deterministic stand-in for GetMusicGenreId, which is itself a pure hash.
        return new PayloadProjector(
            artistMap,
            albumMap,
            name => new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name))));
    }

    private static ItemFacts Track(params string[] artistNames) => new ItemFacts
    {
        Id = TrackId,
        Name = "Song",
        AlbumId = AlbumId,
        RunTimeTicks = 2_134_000_000,
        ArtistNames = artistNames
    };

    [Fact]
    public void TrackCarriesAlbumNameAndArtFromTheAlbumMap()
    {
        var payload = NewProjector().Track(Track("Alpha"));

        Assert.Equal("44444444444444444444444444444444", payload.Id);
        Assert.Equal("Record", payload.AlbumName);
        Assert.Equal("33333333333333333333333333333333", payload.AlbumId);
        Assert.Equal("albumtag", payload.AlbumImageTag);
    }

    [Fact]
    public void ArtistOrderIsPreservedBecauseTheClientUsesTheIndexAsAPosition()
    {
        var forward = NewProjector().Track(Track("Alpha", "Beta"));
        var reversed = NewProjector().Track(Track("Beta", "Alpha"));

        Assert.Equal(
            new[] { "11111111111111111111111111111111", "22222222222222222222222222222222" },
            forward.ArtistIDs);
        Assert.Equal(
            new[] { "22222222222222222222222222222222", "11111111111111111111111111111111" },
            reversed.ArtistIDs);

        // artistId is the first credit, and artistName is the joined display string in order.
        Assert.Equal("11111111111111111111111111111111", forward.ArtistId);
        Assert.Equal("Alpha, Beta", forward.ArtistName);
        Assert.Equal("Beta, Alpha", reversed.ArtistName);
    }

    [Fact]
    public void UnresolvableArtistNamesAreDroppedRatherThanBreakingThePositions()
    {
        // Jellyfin does not always have an artist entity for every credit string.
        var payload = NewProjector().Track(Track("Alpha", "Nobody", "Beta"));

        Assert.Equal(
            new[] { "11111111111111111111111111111111", "22222222222222222222222222222222" },
            payload.ArtistIDs);

        // The display string still shows the credit the library actually has.
        Assert.Equal("Alpha, Nobody, Beta", payload.ArtistName);
    }

    [Fact]
    public void RepeatedArtistCreditsCollapse()
    {
        // The client's relation table is keyed on (item, artist), so a repeat would collapse there
        // anyway and shift every later position.
        var payload = NewProjector().Track(Track("Alpha", "Alpha", "Beta"));

        Assert.Equal(2, payload.ArtistIDs!.Count);
    }

    [Fact]
    public void ArtistNamesResolveCaseInsensitively()
    {
        var payload = NewProjector().Track(Track("ALPHA"));

        Assert.Equal(new[] { "11111111111111111111111111111111" }, payload.ArtistIDs);
    }

    [Fact]
    public void ATrackWithNoArtistsOmitsBothArtistFields()
    {
        var payload = NewProjector().Track(Track());

        Assert.Null(payload.ArtistIDs);
        Assert.Null(payload.ArtistId);
        Assert.Null(payload.ArtistName);
    }

    [Fact]
    public void ATrackWhoseAlbumIsMissingStillProjects()
    {
        // Happens when an album is outside the user's visible libraries, or mid-scan.
        var projector = NewProjector(albums: new Dictionary<Guid, AlbumSummary>());
        var payload = projector.Track(Track("Alpha"));

        Assert.Null(payload.AlbumName);
        Assert.Null(payload.AlbumImageTag);

        // The identifier is still sent: the client can link it once the album arrives.
        Assert.Equal("33333333333333333333333333333333", payload.AlbumId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, null)]
    [InlineData(-5L, null)]
    [InlineData(10_000_000L, 1.0)]
    [InlineData(2_134_000_000L, 213.4)]
    [InlineData(1L, 0.0)]
    public void DurationIsSecondsRoundedToMilliseconds(long? ticks, double? expected)
    {
        Assert.Equal(expected, PayloadProjector.TicksToSeconds(ticks));
    }

    [Fact]
    public void DurationNeverCarriesPointlessPrecision()
    {
        // Full double precision would add several bytes to each of 30,000 track records to express
        // a precision no player uses.
        var seconds = PayloadProjector.TicksToSeconds(2_134_567_891L);

        Assert.Equal(213.457, seconds);
    }

    [Fact]
    public void EmptyGenresAreOmittedEntirely()
    {
        var payload = NewProjector().Track(Track("Alpha") with { GenreNames = Array.Empty<string>() });

        Assert.Null(payload.GenreIDs);
    }

    [Fact]
    public void GenresAreResolvedDeduplicatedAndOrdered()
    {
        var payload = NewProjector().Track(
            Track("Alpha") with { GenreNames = new[] { "Jazz", "Rock", "Jazz", " " } });

        Assert.Equal(2, payload.GenreIDs!.Count);
        Assert.All(payload.GenreIDs, id => Assert.Equal(32, id.Length));
    }

    [Fact]
    public void SortNameIsOmittedWhenItAddsNothing()
    {
        var projector = NewProjector();

        Assert.Null(projector.Track(Track("Alpha") with { SortName = "Song" }).SortName);
        Assert.Null(projector.Track(Track("Alpha") with { SortName = "  " }).SortName);
        Assert.Equal("Sortable", projector.Track(Track("Alpha") with { SortName = "Sortable" }).SortName);
    }

    [Fact]
    public void AlbumPrefersAlbumArtistsButFallsBackToPerformers()
    {
        var projector = NewProjector();
        var album = new ItemFacts { Id = AlbumId, Name = "Record" };

        var withAlbumArtist = projector.Album(
            album with { AlbumArtistNames = new[] { "Alpha" }, ArtistNames = new[] { "Beta" } }, 9);
        Assert.Equal("Alpha", withAlbumArtist.ArtistName);
        Assert.Equal("11111111111111111111111111111111", withAlbumArtist.ArtistId);

        // A missing album-artist tag should not produce "Unknown Artist" on the client.
        var withoutAlbumArtist = projector.Album(album with { ArtistNames = new[] { "Beta" } }, 9);
        Assert.Equal("Beta", withoutAlbumArtist.ArtistName);
        Assert.Equal("22222222222222222222222222222222", withoutAlbumArtist.ArtistId);
    }

    [Fact]
    public void ArtistCarriesTheAlbumArtistFlagTheClientCannotDeriveItself()
    {
        // The snapshot carries every artist, because tracks reference guest credits. Browsing by
        // artist should show only album artists, and nothing else in the stream distinguishes them.
        var projector = NewProjector();
        var facts = new ItemFacts { Id = ArtistA, Name = "Alpha", Overview = "bio" };

        Assert.True(projector.Artist(facts, 3, isAlbumArtist: true).IsAlbumArtist);
        Assert.False(projector.Artist(facts, 0, isAlbumArtist: false).IsAlbumArtist);

        var payload = projector.Artist(facts, 3, isAlbumArtist: true);
        Assert.Equal("bio", payload.Biography);
        Assert.Equal(3, payload.AlbumCount);
    }

    [Fact]
    public void PlaylistEntryCarriesTheWholeTrackPlusItsMembership()
    {
        var playlistId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var entry = NewProjector().PlaylistEntry(Track("Alpha"), playlistId, "entry-1", 3);

        Assert.Equal("99999999999999999999999999999999", entry.PlaylistID);
        Assert.Equal("entry-1", entry.PlaylistEntryID);
        Assert.Equal(3, entry.Position);

        // The client upserts the track from this same record, so it must be complete.
        Assert.Equal("44444444444444444444444444444444", entry.Id);
        Assert.Equal("Song", entry.Name);
        Assert.Equal("Record", entry.AlbumName);
        Assert.Equal(213.4, entry.Duration);
        Assert.Equal(new[] { "11111111111111111111111111111111" }, entry.ArtistIDs);
    }

    [Fact]
    public void UserDataIsOmittedWhenTheUserHasNeverTouchedTheItem()
    {
        var projector = NewProjector();

        Assert.Null(projector.UserData(Track("Alpha")));
        Assert.Null(projector.UserData(Track("Alpha") with { UserData = new UserDataFacts() }));
    }

    [Theory]
    [InlineData(true, 0, 0, false)]
    [InlineData(false, 1, 0, false)]
    [InlineData(false, 0, 500, false)]
    [InlineData(false, 0, 0, true)]
    public void UserDataIsSentWhenAnythingIsSet(bool favorite, int playCount, long position, bool played)
    {
        var facts = Track("Alpha") with
        {
            UserData = new UserDataFacts
            {
                IsFavorite = favorite,
                PlayCount = playCount,
                PlaybackPositionTicks = position,
                Played = played
            }
        };

        var payload = NewProjector().UserData(facts);

        Assert.NotNull(payload);
        Assert.Equal("44444444444444444444444444444444", payload!.Id);
        Assert.Equal(favorite, payload.IsFavorite);
        Assert.Equal(playCount, payload.PlayCount);
        Assert.Equal(position, payload.PlaybackPositionTicks);
    }

    [Fact]
    public void UserDataSendsExplicitFalseAndZeroRatherThanOmitting()
    {
        // The client applies user data with COALESCE, so omitting a field leaves the old value.
        // A partially-set record must therefore still state the fields it is clearing.
        var facts = Track("Alpha") with
        {
            UserData = new UserDataFacts { IsFavorite = false, PlayCount = 0, Played = true }
        };

        var payload = NewProjector().UserData(facts)!;

        Assert.False(payload.IsFavorite);
        Assert.Equal(0, payload.PlayCount);
        Assert.Equal(0, payload.PlaybackPositionTicks);
    }

    [Fact]
    public void FavouriteStateAlsoRidesOnTheItemItself()
    {
        // The client reads isFavorite from item payloads too, so both paths must agree.
        var facts = Track("Alpha") with { UserData = new UserDataFacts { IsFavorite = true } };

        Assert.True(NewProjector().Track(facts).IsFavorite);
    }

    [Fact]
    public void IdentifiersUseJellyfinsDashlessLowercaseForm()
    {
        var id = Guid.Parse("6F3A1B2C-3D4E-5F60-7182-93A4B5C6D7E8");

        Assert.Equal("6f3a1b2c3d4e5f60718293a4b5c6d7e8", PayloadProjector.FormatId(id));
        Assert.DoesNotContain('-', PayloadProjector.FormatId(id));
        Assert.Equal(32, PayloadProjector.FormatId(id).Length);
    }

    [Fact]
    public void ProjectionIsDeterministic()
    {
        // Two builds of an unchanged library must produce identical bytes, which is what makes the
        // snapshot checksum a meaningful assertion.
        var facts = Track("Alpha", "Beta") with { GenreNames = new[] { "Jazz", "Rock" } };

        var first = System.Text.Json.JsonSerializer.Serialize(
            NewProjector().Track(facts), Jellyfin.Plugin.AureliaSync.Wire.WireSchema.JsonOptions);
        var second = System.Text.Json.JsonSerializer.Serialize(
            NewProjector().Track(facts), Jellyfin.Plugin.AureliaSync.Wire.WireSchema.JsonOptions);

        Assert.Equal(first, second);
    }
}
