using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Track stubs written end to end: a .strm per track, no .nfo, the album cover in the album folder (D60).
public sealed class TrackMaterializerTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public TrackMaterializerTests() => _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.Music, "Dischi", "music"));

    public void Dispose() => _fixture.Dispose();

    private static RemoteItem Track(string id, string name, int number, string cover = "c1") => new()
    {
        Id = id,
        Name = name,
        IndexNumber = number,
        ProductionYear = 2020,
        Album = "Album",
        AlbumId = "al1",
        AlbumPrimaryImageTag = cover,
        Artists = ["Artist"],
        AlbumArtists = [new RemoteNamedItem("Artist")],
        Genres = ["Rock"],
        ProviderIds = new Dictionary<string, string?> { ["MusicBrainzTrack"] = "mb-" + id },
        MediaSources =
        [
            new RemoteMediaSource
            {
                Id = id,
                Name = "Track",
                Container = "flac",
                MediaStreams = [new RemoteStreamInfo("Audio", "flac", null, null, 1411000, null, null, true)]
            }
        ]
    };

    private string AlbumPath(RemoteServerInfo server, params string[] parts) => _fixture.PathIn(server, TestLibraries.Music, ["Musica", "Artist", "Album", .. parts]);

    [Fact]
    public async Task ATrackBecomesAStrmWithNoNfoAndItsAlbumFolderGetsTheCover()
    {
        _fixture.Sync.Client.Add(TestLibraries.Music, RemoteItemType.Audio, Track("t1", "Song", 1), Track("t2", "Other", 2));
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Music);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            ["01 - Song.strm", "02 - Other.strm", "folder.jpg"],
            Directory.GetFiles(AlbumPath(a)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(_fixture.Files, path => path.EndsWith(".nfo", StringComparison.Ordinal));
        Assert.Equal(["al1/Primary/0"], _fixture.Sync.Client.ImageReads);
        var stub = Assert.Single(_fixture.Registry.State.Stubs, stub => stub.RelativePath.EndsWith("01 - Song.strm", StringComparison.Ordinal));
        Assert.Equal(StubKind.Track, stub.Kind);
        Assert.StartsWith("http://", StubFixture.Text(AlbumPath(a, "01 - Song.strm")), StringComparison.Ordinal);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task ANewCoverReachesTheAlbumAndNotTheTrack()
    {
        _fixture.Sync.Client.Add(TestLibraries.Music, RemoteItemType.Audio, Track("t1", "Song", 1));
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Music);
        await _fixture.SyncAndMaterializeAsync();
        var strm = AlbumPath(a, "01 - Song.strm");
        var track = new Audio { Id = Guid.NewGuid(), Path = strm, Name = "Song" };
        var album = new MusicAlbum { Id = Guid.NewGuid(), Path = AlbumPath(a), Name = "Album" };
        _fixture.Library.Setup(library => library.FindByPath(strm, false)).Returns(track);
        _fixture.Library.Setup(library => library.FindByPath(AlbumPath(a), true)).Returns(album);
        _fixture.Sync.Client.Items[(TestLibraries.Music, RemoteItemType.Audio)][0] = Track("t1", "Song", 1, cover: "c2");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(AlbumPath(a, "folder.jpg"), album.GetImageInfo(ImageType.Primary, 0)?.Path);
        Assert.Empty(track.GetImages(ImageType.Primary));
        Assert.Equal(2, _fixture.Sync.Client.ImageReads.Count);
    }
}
