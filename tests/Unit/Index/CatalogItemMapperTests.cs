using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public class CatalogItemMapperTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid _serverId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static IndexedItem Map(RemoteItem item, RemoteItemType type)
        => RemoteItemMapper.ToIndexedItem(item, type, "library", _now);

    private static ServerCatalog Catalog(params IndexedItem[] items)
        => new() { ServerId = _serverId, State = ServerSyncState.Complete, Items = items };

    private static IndexedItem Song(string id, params IndexedVersion[] versions) => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Audio,
        LibraryId = "music",
        LastSeenUtc = _now,
        Name = "Song",
        Album = "Album",
        AlbumArtists = ["Band"],
        Artists = ["Band", "Guest"],
        IndexNumber = 4,
        ParentIndexNumber = 2,
        RunTimeTicks = 1_800_000_000,
        ProviderIds = new System.Collections.Generic.Dictionary<string, string>
        {
            ["MusicBrainzRecording"] = "rec",
            ["MusicBrainzTrack"] = "trk",
            ["MusicBrainzAlbum"] = "alb"
        },
        Versions = versions
    };

    [Fact]
    public void ATrackCarriesItsTagsAndMusicBrainzIds()
    {
        var song = Song("t", new IndexedVersion { MediaSourceId = "ms" });

        var track = Assert.Single(CatalogItemMapper.ToTrackItems(Catalog(song, Map(TestItems.Movie("m"), RemoteItemType.Movie))));

        Assert.Equal((_serverId.ToString("N"), "t", "Song", "Album"), (track.ServerId, track.ItemId, track.Title, track.Album));
        Assert.Equal(["Band"], track.AlbumArtists);
        Assert.Equal(["Band", "Guest"], track.Artists);
        Assert.Equal((2, 4, 1_800_000_000L), (track.DiscNumber, track.TrackNumber, track.RunTimeTicks));
        Assert.Equal(("rec", "trk"), (track.RecordingId, track.TrackId));
        Assert.Equal("ms", Assert.Single(track.Versions).MediaSourceId);
        Assert.DoesNotContain(CatalogItemMapper.ToCatalogItems(Catalog(song)), item => item.ItemId == "t");
    }

    [Fact]
    public void TheQualityOfATrackComesFromItsDefaultAudioStream()
    {
        var version = new IndexedVersion
        {
            MediaSourceId = "ms",
            ContainerBitRate = 2_000_000,
            Streams =
            [
                new IndexedTrack { Type = "Video", Codec = "mjpeg", BitRate = 5_000_000, IsDefault = true },
                new IndexedTrack { Type = "Audio", Codec = "mp3", BitRate = 128_000, SampleRate = 44100 },
                new IndexedTrack { Type = "Audio", Codec = "flac", BitRate = 900_000, BitDepth = 24, SampleRate = 96000, IsDefault = true }
            ]
        };

        var quality = Assert.Single(Assert.Single(CatalogItemMapper.ToTrackItems(Catalog(Song("t", version)))).Versions).Quality;

        Assert.Equal(("flac", 24, 96000, 900_000), (quality.Codec, quality.BitDepth, quality.SampleRate, quality.BitRate));
    }

    [Fact]
    public void WithoutADefaultTheFirstAudioStreamCountsAndTheContainerBitrateStandsForAMissingOne()
    {
        var version = new IndexedVersion
        {
            MediaSourceId = "ms",
            ContainerBitRate = 1_000_000,
            Streams = [new IndexedTrack { Type = "Audio", Codec = "alac", BitDepth = 16, SampleRate = 44100 }, new IndexedTrack { Type = "Audio", Codec = "mp3" }]
        };

        var quality = Assert.Single(Assert.Single(CatalogItemMapper.ToTrackItems(Catalog(Song("t", version)))).Versions).Quality;

        Assert.Equal(("alac", 16, 44100, 1_000_000), (quality.Codec, quality.BitDepth, quality.SampleRate, quality.BitRate));
    }

    [Fact]
    public void AnAudioVersionWithoutStreamsKeepsTheContainerBitrateOnly()
    {
        var version = new IndexedVersion { MediaSourceId = "ms", ContainerBitRate = 320_000 };

        var quality = Assert.Single(Assert.Single(CatalogItemMapper.ToTrackItems(Catalog(Song("t", version)))).Versions).Quality;

        Assert.Equal((null, null, null, 320_000), (quality.Codec, quality.BitDepth, quality.SampleRate, quality.BitRate));
    }

    [Fact]
    public void AnEpisodeTakesTitleYearAndIdsFromItsSeries()
    {
        var series = Map(TestItems.Series("s", "Show", 2019), RemoteItemType.Series);
        var episode = Map(TestItems.Episode("e", "s", 2, 3, "Other name"), RemoteItemType.Episode);

        var item = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(series, episode)));

        Assert.Equal(ContentKind.Episode, item.Kind);
        Assert.Equal("Show", item.Title);
        Assert.Equal(2019, item.Year);
        Assert.Equal(new ContentIds("72", "tt71", "71"), item.Ids);
        Assert.Equal((2, 3), (item.SeasonNumber, item.EpisodeNumber));
        Assert.Equal(_serverId.ToString("N"), item.ServerId);
        Assert.Equal("s", item.SeriesId);
    }

    [Theory]
    [InlineData("HDR", true, true)]
    [InlineData("SDR", false, true)]
    [InlineData("Unknown", false, false)]
    public void TheRangeSaysWhetherItIsKnown(string range, bool isHdr, bool isKnown)
    {
        var film = TestItems.Movie("m") with { MediaSources = [TestItems.Source("m", 1080, 1000, range: range)] };

        var quality = Assert.Single(Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(Map(film, RemoteItemType.Movie)))).Versions).Quality;

        Assert.Equal((isHdr, isKnown), (quality.IsHdr, quality.IsRangeKnown));
    }

    [Fact]
    public void AFilmCarriesTheMetadataThatConfirmsAMatch()
    {
        var film = Map(TestItems.Movie("m", "Film", 2020, "1"), RemoteItemType.Movie) with
        {
            OriginalTitle = "Original",
            RunTimeTicks = 600,
            Overview = "Plot",
            Genres = ["Drama"],
            Studios = ["Studio"],
            People = [new IndexedPerson("Anna", "Role", "Actor"), new IndexedPerson(null, null, null)]
        };

        var metadata = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(film))).Metadata;

        Assert.Equal("Original", metadata.OriginalTitle);
        Assert.Equal(600, metadata.RunTimeTicks);
        Assert.Equal("Plot", metadata.Overview);
        Assert.Equal(["Drama"], metadata.Genres);
        Assert.Equal(["Studio"], metadata.Studios);
        Assert.Equal(["Anna"], metadata.People.Value);
    }

    [Fact]
    public void AFileWithSeveralEpisodesKeepsItsLastNumber()
    {
        var episode = Map(TestItems.Episode("e", "s", 1, 2) with { IndexNumberEnd = 3 }, RemoteItemType.Episode);

        Assert.Equal(3, episode.IndexNumberEnd);
        Assert.Equal(3, Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(episode))).EpisodeNumberEnd);
    }

    [Fact]
    public void AMusicVideoEntersTheDedupAsOneTitle_WithItsArtistsIdsYearAndVersions()
    {
        var clip = Map(TestItems.Movie("v", "Clip", 2019, "9") with { Artists = ["Artist", "Guest"] }, RemoteItemType.MusicVideo);

        var item = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(clip)));

        Assert.Equal(ContentKind.MusicVideo, item.Kind);
        Assert.Equal(("v", "Clip", 2019), (item.ItemId, item.Title, item.Year));
        Assert.Null(item.SeriesId);
        Assert.Equal(["Artist", "Guest"], item.Artists);
        Assert.Equal("9", item.Ids.Tmdb);
        Assert.Equal(1080, Assert.Single(item.Versions).Quality.Height);
    }

    [Fact]
    public void AnEpisodeWithoutItsSeriesKeepsTheSeriesNameAndNoYear()
    {
        var episode = Map(TestItems.Episode("e", "gone", 1, 1), RemoteItemType.Episode);

        var item = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(episode)));

        Assert.Equal("Show", item.Title);
        Assert.Null(item.Year);
        Assert.Equal(ContentIds.None, item.Ids);
    }

    [Theory]
    [InlineData(RemoteItemType.Audio)]
    [InlineData(RemoteItemType.Video)]
    [InlineData(RemoteItemType.Photo)]
    [InlineData(RemoteItemType.PhotoAlbum)]
    [InlineData(RemoteItemType.Folder)]
    [InlineData(RemoteItemType.Book)]
    [InlineData(RemoteItemType.AudioBook)]
    public void AnEntryOfTheNewTypesStaysOutOfTheDedup_ApartFromMusicVideos(RemoteItemType type)
    {
        var entry = Map(TestItems.Movie("n", "Same", 2020, "1"), type);
        var film = Map(TestItems.Movie("m", "Same", 2020, "1"), RemoteItemType.Movie);

        var item = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(entry, film)));

        Assert.Equal("m", item.ItemId);
    }

    [Fact]
    public void TheFilmsAndEpisodesOfAMixedLibraryEnterTheDedupBesideItsMusic()
    {
        var film = RemoteItemMapper.ToIndexedItem(TestItems.Movie("m", "Film", 2020, "1"), RemoteItemType.Movie, "mixed-library", _now);
        var series = RemoteItemMapper.ToIndexedItem(TestItems.Series("s", "Show", 2019), RemoteItemType.Series, "mixed-library", _now);
        var episode = RemoteItemMapper.ToIndexedItem(TestItems.Episode("e", "s", 1, 1), RemoteItemType.Episode, "mixed-library", _now);
        var track = RemoteItemMapper.ToIndexedItem(TestItems.Movie("t", "Track", 2020, "2"), RemoteItemType.Audio, "mixed-library", _now);

        var items = CatalogItemMapper.ToCatalogItems(Catalog(film, series, episode, track));

        Assert.Equal([("e", ContentKind.Episode), ("m", ContentKind.Movie)], items.Select(item => (item.ItemId, item.Kind)).Order());
    }

    [Fact]
    public void SeriesAndSeasonsAreNotItemsOfTheirOwn()
    {
        var items = CatalogItemMapper.ToCatalogItems(Catalog(
            Map(TestItems.Series("s"), RemoteItemType.Series),
            Map(TestItems.Season("s1", "s", 1), RemoteItemType.Season)));

        Assert.Empty(items);
    }

    [Fact]
    public void EachMediaSourceIsOneVersion()
    {
        var movie = TestItems.Movie("m") with
        {
            MediaSources = [TestItems.Source("a", 1080, 5_000_000), TestItems.Source("b", 720, 2_000_000)]
        };

        var item = Assert.Single(CatalogItemMapper.ToCatalogItems(Catalog(Map(movie, RemoteItemType.Movie))));

        Assert.Equal(["a", "b"], item.Versions.Select(version => version.MediaSourceId));
        Assert.Equal(1080, item.Versions[0].Quality.Height);
        Assert.Equal(720, item.Versions[1].Quality.Height);
    }

    [Fact]
    public void QualityComesFromTheVideoStreamUncorrected()
    {
        var movie = TestItems.Movie("m") with { MediaSources = [TestItems.Source("a", 1080, 2_650_000, "av1", "HDR")] };

        var quality = CatalogItemMapper.ToCatalogItems(Catalog(Map(movie, RemoteItemType.Movie)))[0].Versions[0].Quality;

        Assert.Equal(new SourceQuality(1080, true, 2_650_000, "av1"), quality);
    }

    [Theory]
    [InlineData("SDR", false)]
    [InlineData("Unknown", false)]
    [InlineData("HDR", true)]
    public void OnlyAnHdrRangeCountsAsHdr(string range, bool expected)
    {
        var movie = TestItems.Movie("m") with { MediaSources = [TestItems.Source("a", 1080, 1000, "hevc", range)] };

        Assert.Equal(expected, CatalogItemMapper.ToCatalogItems(Catalog(Map(movie, RemoteItemType.Movie)))[0].Versions[0].Quality.IsHdr);
    }

    [Fact]
    public void ASourceWithoutVideoHasNoQualityValues()
    {
        var movie = TestItems.Movie("m") with { MediaSources = [new RemoteMediaSource { Id = "strm", Container = "strm", IsRemote = true, Protocol = "Http" }] };

        var indexed = Map(movie, RemoteItemType.Movie);

        Assert.True(indexed.Versions[0].IsRemote);
        Assert.Equal(new SourceQuality(null, false, null, null) { IsRangeKnown = false }, CatalogItemMapper.ToCatalogItems(Catalog(indexed))[0].Versions[0].Quality);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void WhetherAVersionHasSegmentsIsKept(bool? hasSegments)
    {
        var movie = TestItems.Movie("m") with { MediaSources = [TestItems.Source("a", 1080, 1000) with { HasSegments = hasSegments }] };

        var indexed = Map(movie, RemoteItemType.Movie);

        Assert.Equal(hasSegments, indexed.Versions[0].HasSegments);
    }

    [Fact]
    public void AllProviderIdsAreKeptAndEmptyOnesDropped()
    {
        var series = TestItems.Series("s") with
        {
            ProviderIds = new System.Collections.Generic.Dictionary<string, string?>
            {
                ["Tvdb"] = "1",
                ["Custom"] = "2",
                ["Anything"] = "3",
                ["Empty"] = string.Empty,
                ["Null"] = null
            }
        };

        var ids = Map(series, RemoteItemType.Series).ProviderIds;

        Assert.Equal(3, ids.Count);
        Assert.Equal("2", ids["Custom"]);
        Assert.Equal("3", ids["Anything"]);
    }

    [Fact]
    public void TheFieldsOfAFullNfoAreCopied()
    {
        var movie = new RemoteItem
        {
            Id = "m",
            Overview = "Plot",
            Genres = ["Drama"],
            People = [new RemotePerson("Name", "Role", "Actor")],
            Studios = [new RemoteNamedItem("Studio"), new RemoteNamedItem(null)],
            OfficialRating = "PG",
            CommunityRating = 7.5f,
            CriticRating = 80f,
            RunTimeTicks = 600,
            Etag = "etag",
            DateCreated = _now,
            ImageTags = new System.Collections.Generic.Dictionary<string, string> { ["Primary"] = "tag" },
            BackdropImageTags = ["backdrop"]
        };

        var item = Map(movie, RemoteItemType.Movie);

        Assert.Equal("Plot", item.Overview);
        Assert.Equal(["Drama"], item.Genres);
        Assert.Equal(new IndexedPerson("Name", "Role", "Actor"), Assert.Single(item.People));
        Assert.Equal(["Studio"], item.Studios);
        Assert.Equal(("PG", 7.5f, 80f), (item.OfficialRating, item.CommunityRating, item.CriticRating));
        Assert.Equal(600, item.RunTimeTicks);
        Assert.Equal(("etag", (DateTimeOffset?)_now), (item.Etag, item.DateCreated));
        Assert.Equal("tag", item.ImageTags["Primary"]);
        Assert.Equal(["backdrop"], item.BackdropImageTags);
    }
}
