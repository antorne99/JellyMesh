using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
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

    private static RemoteItem Track(string id, string name, int number, string cover = "c1", string albumId = "al1", string artist = "Artist") => new()
    {
        Id = id,
        Name = name,
        IndexNumber = number,
        ProductionYear = 2020,
        Album = "Album",
        AlbumId = albumId,
        AlbumPrimaryImageTag = cover,
        Artists = [artist],
        AlbumArtists = [new RemoteNamedItem(artist)],
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

    [Fact]
    public async Task ATrackThatIsTheStubOfAnotherJellyMeshGetsNoStub()
    {
        var federated = Track("t1", "Song", 1);
        federated = federated with { MediaSources = [federated.MediaSources![0] with { Path = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature") }] };
        _fixture.Sync.Client.Add(TestLibraries.Music, RemoteItemType.Audio, federated, Track("t2", "Other", 2));
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Music);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["02 - Other.strm", "folder.jpg"], Directory.GetFiles(AlbumPath(a)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheLastTracksOfAnArtistTakeTheirAlbumAndArtistFoldersWithTheCover()
    {
        _fixture.Sync.Client.Add(TestLibraries.Music, RemoteItemType.Audio, Track("t1", "Song", 1), Track("t2", "Other", 2), Track("t3", "Kept", 1, albumId: "al2", artist: "Band"));
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Music);
        await _fixture.SyncAndMaterializeAsync();
        Assert.True(File.Exists(AlbumPath(a, "folder.jpg")));
        _fixture.Sync.Client.Items[(TestLibraries.Music, RemoteItemType.Audio)].RemoveAll(item => item.Id is "t1" or "t2");

        await _fixture.SyncAndMaterializeAsync();

        Assert.False(Directory.Exists(_fixture.PathIn(a, TestLibraries.Music, "Musica", "Artist")));
        Assert.True(File.Exists(_fixture.PathIn(a, TestLibraries.Music, "Musica", "Band", "Album", "01 - Kept.strm")));
        Assert.Equal(["Kept"], _fixture.Registry.State.Stubs.Select(stub => Path.GetFileNameWithoutExtension(stub.RelativePath)[5..]));
    }

    [Fact]
    public async Task WhenTheTrackThatCarriesTheCoverGoesTheNextOneCarriesIt()
    {
        _fixture.Sync.Client.Add(TestLibraries.Music, RemoteItemType.Audio, Track("t1", "Song", 1), Track("t2", "Other", 2));
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Music);
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[(TestLibraries.Music, RemoteItemType.Audio)].RemoveAll(item => item.Id is "t1");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["02 - Other.strm", "folder.jpg"], Directory.GetFiles(AlbumPath(a)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("Primary=c1;", Assert.Single(_fixture.Registry.State.Stubs).ImageTag);
    }

    [Fact]
    public async Task ATrackStubIsReadBackAsATrackAfterARestart()
    {
        _fixture.Sync.Client.Add(TestLibraries.Music, RemoteItemType.Audio, Track("t1", "Song", 1));
        await _fixture.Sync.AddServerAsync("A", TestLibraries.Music);
        await _fixture.SyncAndMaterializeAsync();

        using var restarted = new StubRegistry(_fixture.Sync.Database, _fixture.Sync.Store, new CapturingLogger<StubRegistry>());

        Assert.Equal(StubKind.Track, Assert.Single(restarted.State.Stubs).Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChangedTrackReachesItsIndexedItemLockedOnlyWithProtection(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        _fixture.Sync.Client.Add(TestLibraries.Music, RemoteItemType.Audio, Track("t1", "Song", 1));
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Music);
        await _fixture.SyncAndMaterializeAsync();
        var strm = AlbumPath(a, "01 - Song.strm");
        var track = new Audio { Id = Guid.NewGuid(), Path = strm, Name = "Song" };
        _fixture.Library.Setup(library => library.FindByPath(strm, false)).Returns(track);
        _fixture.Sync.Client.Items[(TestLibraries.Music, RemoteItemType.Audio)][0] = Track("t1", "Song (Live)", 1) with
        {
            Album = "Album (Live)",
            Artists = ["Artist", "Guest"],
            ParentIndexNumber = 2,
            Genres = ["Live"]
        };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(("Song (Live)", "Album (Live)", 1, 2, 2020), (track.Name, track.Album, track.IndexNumber, track.ParentIndexNumber, track.ProductionYear));
        Assert.Equal(["Artist", "Guest"], track.Artists);
        Assert.Equal(["Artist"], track.AlbumArtists);
        Assert.Equal(["Live"], track.Genres);
        Assert.Equal("mb-t1", track.ProviderIds["MusicBrainzTrack"]);
        Assert.Equal(isProtected, track.IsLocked);
        Assert.True(File.Exists(strm));
    }
}
