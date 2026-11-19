using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The stub providers on music videos and home videos: no probe and the technical data, as for films; metadata and lock
// come from their .nfo in the same first refresh (spec §3.12).
public sealed class VideoProviderTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private StubBeforeProbeProvider Before() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private StubAfterProbeProvider After() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private static MetadataRefreshOptions Options() => new(new Mock<IDirectoryService>().Object) { EnableRemoteContentProbe = true };

    private static RemoteMediaSource Source(string id, long runTime) => new()
    {
        Id = id,
        Container = "mkv",
        RunTimeTicks = runTime,
        MediaStreams =
        [
            new RemoteStreamInfo("Video", "h264", 1080, 1920, 5_000_000, "SDR", "SDR", true) { Index = 0 },
            new RemoteStreamInfo("Audio", "aac", null, null, 192000, null, null, true) { Index = 1 }
        ]
    };

    private async Task<(string MusicVideo, string HomeVideo)> StubsAsync()
    {
        _fixture.Sync.Client.Libraries.Add(new(TestLibraries.MusicVideos, "Clip", "musicvideos"));
        _fixture.Sync.Client.Libraries.Add(new(TestLibraries.HomeVideos, "Casa", "homevideos"));
        _fixture.Sync.Client.Add(TestLibraries.MusicVideos, RemoteItemType.MusicVideo, new RemoteItem
        {
            Id = "mv",
            Name = "Song",
            ProductionYear = 2019,
            Artists = ["Singer"],
            Album = "Album",
            ProviderIds = new Dictionary<string, string?> { ["Imvdb"] = "i1" },
            MediaSources = [Source("mv", 2_400_000_000)]
        });
        _fixture.Sync.Client.Add(TestLibraries.HomeVideos, RemoteItemType.Video, new RemoteItem
        {
            Id = "v1",
            Name = "Beach day",
            ParentId = TestLibraries.HomeVideos,
            MediaSources = [Source("v1", 600_000_000)]
        });
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.MusicVideos, TestLibraries.HomeVideos);
        await _fixture.SyncAndMaterializeAsync();
        return (
            _fixture.PathIn(a, TestLibraries.MusicVideos, "Video musicali", "Song (2019)", "Song (2019) - A.strm"),
            _fixture.PathIn(a, TestLibraries.HomeVideos, "Video domestici", "Beach day.strm"));
    }

    [Fact]
    public void TheyRunOnMusicVideosAndHomeVideos()
    {
        Assert.IsAssignableFrom<ICustomMetadataProvider<MusicVideo>>(Before());
        Assert.IsAssignableFrom<ICustomMetadataProvider<Video>>(Before());
        Assert.IsAssignableFrom<ICustomMetadataProvider<MusicVideo>>(After());
        Assert.IsAssignableFrom<ICustomMetadataProvider<Video>>(After());
    }

    [Fact]
    public async Task ANewMusicVideoGetsItsDurationAndStreamsWithoutAProbeAndStaysUnlockedForItsNfo()
    {
        var (path, _) = await StubsAsync();
        var item = new MusicVideo { Id = Guid.NewGuid(), Path = path, Name = "Song (2019) - A" };
        var options = Options();

        var result = await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.False(options.EnableRemoteContentProbe);
        Assert.Equal((2_400_000_000L, "mkv"), (item.RunTimeTicks, item.Container));
        Assert.False(item.IsLocked);
        _fixture.Streams.Verify(streams => streams.SaveMediaStreams(item.Id, It.Is<IReadOnlyList<MediaStream>>(list => list.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ANewHomeVideoGetsItsDurationAndStreamsWithoutAProbeAndStaysUnlockedForItsNfo()
    {
        var (_, path) = await StubsAsync();
        var item = new Video { Id = Guid.NewGuid(), Path = path, Name = "Beach day" };
        var options = Options();

        var result = await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.False(options.EnableRemoteContentProbe);
        Assert.Equal((600_000_000L, "mkv"), (item.RunTimeTicks, item.Container));
        Assert.False(item.IsLocked);
        _fixture.Streams.Verify(streams => streams.SaveMediaStreams(item.Id, It.Is<IReadOnlyList<MediaStream>>(list => list.Count == 2), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnUnchangedVideoIsNotSavedAgain()
    {
        var (musicVideo, homeVideo) = await StubsAsync();
        BaseItem[] items = [new MusicVideo { Id = Guid.NewGuid(), Path = musicVideo }, new Video { Id = Guid.NewGuid(), Path = homeVideo }];
        foreach (var item in items)
        {
            await Fetch(item);
        }

        Assert.All(items, item => Assert.Equal(ItemUpdateType.None, Fetch(item).Result));
    }

    [Fact]
    public async Task TheSecondProviderRestoresTheStreamsOfAHomeVideo()
    {
        var (_, path) = await StubsAsync();
        var item = new Video { Id = Guid.NewGuid(), Path = path };

        await After().FetchAsync(item, Options(), CancellationToken.None);

        _fixture.Streams.Verify(streams => streams.SaveMediaStreams(item.Id, It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AVideoOutsideTheFederatedFolderIsLeftAlone()
    {
        await StubsAsync();
        var item = new Video { Id = Guid.NewGuid(), Path = Path.Combine(Path.GetTempPath(), "elsewhere", "Beach day.mkv"), Name = "Beach day" };
        var options = Options();

        Assert.Equal(ItemUpdateType.None, await Before().FetchAsync(item, options, CancellationToken.None));
        Assert.True(options.EnableRemoteContentProbe);
        Assert.Null(item.RunTimeTicks);
    }

    private Task<ItemUpdateType> Fetch(BaseItem item) => item switch
    {
        MusicVideo musicVideo => Before().FetchAsync(musicVideo, Options(), CancellationToken.None),
        Video video => Before().FetchAsync(video, Options(), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(item))
    };
}
