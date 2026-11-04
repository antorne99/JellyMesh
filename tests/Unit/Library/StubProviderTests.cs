using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class StubProviderTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private StubBeforeProbeProvider Before() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private StubAfterProbeProvider After() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private static MetadataRefreshOptions Options() => new(new Mock<IDirectoryService>().Object) { EnableRemoteContentProbe = true };

    private static RemoteMediaSource FullSource(string id) => new()
    {
        Id = id,
        Container = "mkv",
        Bitrate = 4_000_000,
        RunTimeTicks = 1_234_567_890,
        Size = 99,
        Timestamp = "Valid",
        MediaStreams =
        [
            new RemoteStreamInfo("Subtitle", "subrip", null, null, null, null, null, false) { Index = 0, IsExternal = true, Language = "ita" },
            new RemoteStreamInfo("Video", "h264", 1080, 1920, 3_000_000, "SDR", "SDR", true) { Index = 2, Profile = "High", Level = 40, PixelFormat = "yuv420p" },
            new RemoteStreamInfo("Audio", "ac3", null, null, 192000, null, null, true) { Index = 3, Channels = 6, ChannelLayout = "5.1", SampleRate = 48000 }
        ]
    };

    private async Task<string> FilmStubAsync()
    {
        var film = TestItems.Movie("m") with
        {
            MediaSources = [FullSource("m")],
            Chapters = [new RemoteChapter("Uno", 0), new RemoteChapter("Due", 300_000_000)]
        };
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        return _fixture.Path_("Film", "Film (2020)", "Film (2020).strm");
    }

    [Fact]
    public void TheyRunOnLockedItemsAroundTheProbe()
    {
        foreach (var provider in new StubMetadataProvider[] { Before(), After() })
        {
            Assert.IsAssignableFrom<IPreRefreshProvider>(provider);
            Assert.IsAssignableFrom<IForcedProvider>(provider);
            Assert.IsAssignableFrom<ICustomMetadataProvider<Movie>>(provider);
            Assert.IsAssignableFrom<ICustomMetadataProvider<Episode>>(provider);
        }

        Assert.True(Before().Order < 100);
        Assert.True(After().Order > 100);
    }

    [Fact]
    public async Task TheProbeIsOffAndTheDataComesFromTheIndex()
    {
        var path = await FilmStubAsync();
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        var options = Options();

        var result = await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.False(options.EnableRemoteContentProbe);
        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.Equal(1_234_567_890, item.RunTimeTicks);
        Assert.Equal(4_000_000, item.TotalBitrate);
        Assert.Equal("mkv", item.Container);
        _fixture.Streams.Verify(
            streams => streams.SaveMediaStreams(
                item.Id,
                It.Is<IReadOnlyList<MediaStream>>(list => list.Select(stream => stream.Index).SequenceEqual(new[] { 0, 2, 3 })),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ALockedItemIsHandledTheSame()
    {
        var path = await FilmStubAsync();
        var item = new Movie { Id = Guid.NewGuid(), Path = path, IsLocked = true };
        var options = Options();

        await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.False(options.EnableRemoteContentProbe);
        Assert.Equal(1_234_567_890, item.RunTimeTicks);
    }

    [Fact]
    public async Task AnUnchangedItemIsNotSavedAgain()
    {
        var path = await FilmStubAsync();
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, await Before().FetchAsync(item, Options(), CancellationToken.None));
    }

    [Fact]
    public async Task TheSecondProviderRestoresStreamsAndChapters()
    {
        var path = await FilmStubAsync();
        var item = new Movie { Id = Guid.NewGuid(), Path = path };

        await After().FetchAsync(item, Options(), CancellationToken.None);

        _fixture.Streams.Verify(streams => streams.SaveMediaStreams(item.Id, It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()), Times.Once);
        _fixture.Chapters.Verify(
            chapters => chapters.SaveChapters(item, It.Is<IReadOnlyList<ChapterInfo>>(list => list.Count == 2 && list[1].Name == "Due" && list[1].StartPositionTicks == 300_000_000)),
            Times.Once);
    }

    [Fact]
    public async Task AFileOutsideTheFolderIsLeftAlone()
    {
        await FilmStubAsync();
        var item = new Movie { Id = Guid.NewGuid(), Path = "/media/film/Local.mkv", RunTimeTicks = 5 };
        var options = Options();

        var result = await Before().FetchAsync(item, options, CancellationToken.None);
        await After().FetchAsync(item, options, CancellationToken.None);

        Assert.True(options.EnableRemoteContentProbe);
        Assert.Equal(ItemUpdateType.None, result);
        Assert.Equal(5, item.RunTimeTicks);
        _fixture.Streams.Verify(streams => streams.SaveMediaStreams(item.Id, It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AStrmElsewhereIsProbedAsUsual()
    {
        await FilmStubAsync();
        var item = new Movie { Id = Guid.NewGuid(), Path = "/media/film/Other.strm" };
        var options = Options();

        await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.True(options.EnableRemoteContentProbe);
    }

    [Fact]
    public async Task AStrmTheRegistryDoesNotKnowIsStillNotProbed()
    {
        await FilmStubAsync();
        var item = new Movie { Id = Guid.NewGuid(), Path = _fixture.Path_("Film", "Unknown", "Unknown.strm") };
        var options = Options();

        var result = await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.False(options.EnableRemoteContentProbe);
        Assert.Equal(ItemUpdateType.None, result);
    }

    [Fact]
    public async Task AStubMissingFromTheIndexKeepsWhatItHas()
    {
        var path = await FilmStubAsync();
        var item = new Movie { Id = Guid.NewGuid(), Path = path, RunTimeTicks = 7 };
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].Clear();
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);

        await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(7, item.RunTimeTicks);
    }

    [Fact]
    public async Task AnEpisodeGetsItsNumbersBackAtBothEnds()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 2, 3));
        await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_("Serie", "Show (2020)", "Season 02", "Show S02E03.strm");

        var early = new Episode { Id = Guid.NewGuid(), Path = path };
        await Before().FetchAsync(early, Options(), CancellationToken.None);
        Assert.Equal((2, 3), (early.ParentIndexNumber, early.IndexNumber));

        var late = new Episode { Id = Guid.NewGuid(), Path = path };
        var result = await new StubEpisodeNumbersProvider(_fixture.Registry).FetchAsync(late, Options(), CancellationToken.None);
        Assert.Equal((2, 3), (late.ParentIndexNumber, late.IndexNumber));
        Assert.Equal(ItemUpdateType.MetadataImport, result);
    }

    [Fact]
    public void TheLastNumberIsWrittenAndClearedWithTheOthers()
    {
        var stub = new StubEntry { Id = "s", Kind = StubKind.Episode, RelativePath = "r", IdentityKey = "k", Sources = [], SeasonNumber = 1, EpisodeNumber = 2, EpisodeNumberEnd = 3 };
        var episode = new Episode { ParentIndexNumber = 1, IndexNumber = 2 };

        Assert.True(StubTechnicalData.WriteNumbers(episode, stub));
        Assert.Equal(3, episode.IndexNumberEnd);
        Assert.False(StubTechnicalData.WriteNumbers(episode, stub));

        Assert.True(StubTechnicalData.WriteNumbers(episode, stub with { EpisodeNumberEnd = null }));
        Assert.Null(episode.IndexNumberEnd);
    }

    [Fact]
    public void TheLateNumbersProviderIsForcedAndNotPreRefresh()
    {
        Assert.True(typeof(IForcedProvider).IsAssignableFrom(typeof(StubEpisodeNumbersProvider)));
        Assert.False(typeof(IPreRefreshProvider).IsAssignableFrom(typeof(StubEpisodeNumbersProvider)));
    }

    [Fact]
    public void AnExternalStreamGetsAMarkerAndNoRemotePath()
    {
        var first = StubTechnicalData.ToMediaStream(new IndexedTrack { Type = "Subtitle", Index = 0, IsExternal = true, Codec = "subrip" }, "stub")!;
        var second = StubTechnicalData.ToMediaStream(new IndexedTrack { Type = "Subtitle", Index = 1, IsExternal = true, Codec = "subrip" }, "stub")!;

        Assert.True(first.IsExternal);
        Assert.Equal("jellymesh-external-subtitle:stub:0", first.Path);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(MediaStreamType.Subtitle, first.Type);
    }

    [Fact]
    public void AnEmbeddedStreamHasNoPathSoItCountsAsPartOfTheFile()
    {
        Assert.Null(StubTechnicalData.ToMediaStream(new IndexedTrack { Type = "Audio", Index = 3 }, "stub")!.Path);
    }

    [Theory]
    [InlineData("Unknown", 1)]
    [InlineData("Video", null)]
    public void AStreamThatCannotBeDescribedIsSkipped(string type, int? index)
    {
        Assert.Null(StubTechnicalData.ToMediaStream(new IndexedTrack { Type = type, Index = index }, "stub"));
    }
}
