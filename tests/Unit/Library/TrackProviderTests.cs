using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The stub providers on a track: stock Jellyfin reads no tag from it, so every field comes from the plugin (F48, D60).
public sealed class TrackProviderTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public TrackProviderTests() => _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.Music, "Dischi", "music"));

    public void Dispose() => _fixture.Dispose();

    private StubBeforeProbeProvider Before() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private StubAfterProbeProvider After() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private static MetadataRefreshOptions Options() => new(new Mock<IDirectoryService>().Object) { EnableRemoteContentProbe = true };

    private async Task<string> TrackStubAsync()
    {
        _fixture.Sync.Client.Add(TestLibraries.Music, RemoteItemType.Audio, new RemoteItem
        {
            Id = "t1",
            Name = "Song",
            IndexNumber = 3,
            ParentIndexNumber = 2,
            ProductionYear = 2020,
            Album = "Album",
            AlbumId = "al1",
            Artists = ["Artist", "Guest"],
            AlbumArtists = [new RemoteNamedItem("Artist")],
            Genres = ["Rock"],
            ProviderIds = new Dictionary<string, string?> { ["MusicBrainzTrack"] = "mb-t1", ["MusicBrainzAlbum"] = "mb-al1" },
            MediaSources =
            [
                new RemoteMediaSource
                {
                    Id = "t1",
                    Container = "flac",
                    Bitrate = 1_411_000,
                    RunTimeTicks = 1_800_000_000,
                    MediaStreams = [new RemoteStreamInfo("Audio", "flac", null, null, 1_411_000, null, null, true) { Index = 0, Channels = 2, SampleRate = 96000, BitDepth = 24 }]
                }
            ]
        });
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Music);
        await _fixture.SyncAndMaterializeAsync();
        return _fixture.PathIn(a, TestLibraries.Music, "Musica", "Artist", "Album", "2-03 - Song.strm");
    }

    [Fact]
    public void TheyRunOnTracks()
    {
        Assert.IsAssignableFrom<ICustomMetadataProvider<Audio>>(Before());
        Assert.IsAssignableFrom<ICustomMetadataProvider<Audio>>(After());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewTrackGetsItsFieldsDurationAndStreamFromTheIndexAndIsLockedOnlyWithProtection(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        var path = await TrackStubAsync();
        var item = new Audio { Id = Guid.NewGuid(), Path = path, Name = "2-03 - Song" };
        var options = Options();

        var result = await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.False(options.EnableRemoteContentProbe);
        Assert.Equal(("Song", "Album", 3, 2, 2020), (item.Name, item.Album, item.IndexNumber, item.ParentIndexNumber, item.ProductionYear));
        Assert.Equal(["Artist", "Guest"], item.Artists);
        Assert.Equal(["Artist"], item.AlbumArtists);
        Assert.Equal(["Rock"], item.Genres);
        Assert.Equal("mb-al1", item.ProviderIds["MusicBrainzAlbum"]);
        Assert.Equal(isProtected, item.IsLocked);
        Assert.Equal(1_800_000_000, item.RunTimeTicks);
        Assert.Equal("flac", item.Container);
        _fixture.Streams.Verify(
            streams => streams.SaveMediaStreams(
                item.Id,
                It.Is<IReadOnlyList<MediaStream>>(list => list.Single().Type == MediaStreamType.Audio && list.Single().SampleRate == 96000),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AnUnchangedTrackIsNotSavedAgain()
    {
        var path = await TrackStubAsync();
        var item = new Audio { Id = Guid.NewGuid(), Path = path };
        await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, await Before().FetchAsync(item, Options(), CancellationToken.None));
    }

    [Fact]
    public async Task TheSecondProviderRestoresTheAudioStream()
    {
        var path = await TrackStubAsync();
        var item = new Audio { Id = Guid.NewGuid(), Path = path };

        await After().FetchAsync(item, Options(), CancellationToken.None);

        _fixture.Streams.Verify(streams => streams.SaveMediaStreams(item.Id, It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
