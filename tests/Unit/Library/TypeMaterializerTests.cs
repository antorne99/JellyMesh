using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class TypeMaterializerTests : IDisposable
{
    private static readonly DateTimeOffset _shot = new(2021, 7, 8, 0, 0, 0, TimeSpan.Zero);

    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<RemoteServerInfo> AddVideosAsync()
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
            ImageTags = new Dictionary<string, string> { ["Primary"] = "p1" },
            MediaSources = [TestItems.Source("mv", 1080, 5_000_000)]
        });
        _fixture.Sync.Client.Add(TestLibraries.HomeVideos, RemoteItemType.Folder, new RemoteItem { Id = "f1", Name = "Trips", ParentId = TestLibraries.HomeVideos });
        _fixture.Sync.Client.Add(TestLibraries.HomeVideos, RemoteItemType.Video, new RemoteItem
        {
            Id = "v1",
            Name = "Beach day",
            ParentId = "f1",
            PremiereDate = _shot,
            ImageTags = new Dictionary<string, string> { ["Primary"] = "p2" },
            MediaSources = [TestItems.Source("v1", 1080, 5_000_000)]
        });
        return await _fixture.Sync.AddServerAsync("A", TestLibraries.MusicVideos, TestLibraries.HomeVideos);
    }

    [Fact]
    public async Task AMusicVideoAndAHomeVideoBecomeStrmNfoAndImagesInTheirTypeFolders()
    {
        var a = await AddVideosAsync();

        await _fixture.SyncAndMaterializeAsync();

        var clip = StubFixture.InLibrary(a, TestLibraries.MusicVideos, "Video musicali");
        var home = StubFixture.InLibrary(a, TestLibraries.HomeVideos, "Video domestici");
        Assert.Superset(
            new[]
            {
                Path.Combine(clip, ".jellymesh"),
                Path.Combine(clip, "Song (2019)", "Song (2019) - A.strm"),
                Path.Combine(clip, "Song (2019)", "Song (2019) - A.nfo"),
                Path.Combine(clip, "Song (2019)", "poster.jpg"),
                Path.Combine(home, ".jellymesh"),
                Path.Combine(home, "Trips", "Beach day.strm"),
                Path.Combine(home, "Trips", "Beach day.nfo"),
                Path.Combine(home, "Trips", "Beach day-thumb.jpg")
            }.ToHashSet(),
            _fixture.Files.ToHashSet());
        Assert.DoesNotContain(_fixture.Files, file => file.EndsWith("movie.nfo", StringComparison.Ordinal));
        var nfo = XDocument.Load(Path.Combine(_fixture.Root, clip, "Song (2019)", "Song (2019) - A.nfo")).Root!;
        Assert.Equal(("musicvideo", "Singer", "Album"), (nfo.Name.LocalName, nfo.Element("artist")?.Value, nfo.Element("album")?.Value));
        var homeNfo = XDocument.Load(Path.Combine(_fixture.Root, home, "Trips", "Beach day.nfo")).Root!;
        Assert.Equal(("movie", "2021-07-08"), (homeNfo.Name.LocalName, homeNfo.Element("premiered")?.Value));
        Assert.Equal([StubKind.MusicVideo, StubKind.HomeVideo], _fixture.Registry.State.Stubs.Select(stub => stub.Kind).Order());
    }

    [Fact]
    public async Task AHomeVideoGoneFromItsServerTakesItsFilesAndFolder()
    {
        var a = await AddVideosAsync();
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[(TestLibraries.HomeVideos, RemoteItemType.Video)].Clear();
        _fixture.Sync.Client.Add(TestLibraries.HomeVideos, RemoteItemType.Video, new RemoteItem
        {
            Id = "v2",
            Name = "Other",
            ParentId = TestLibraries.HomeVideos,
            MediaSources = [TestItems.Source("v2", 1080, 5_000_000)]
        });

        await _fixture.SyncAndMaterializeAsync();

        var home = StubFixture.InLibrary(a, TestLibraries.HomeVideos, "Video domestici");
        Assert.False(Directory.Exists(Path.Combine(_fixture.Root, home, "Trips")));
        Assert.True(File.Exists(Path.Combine(_fixture.Root, home, "Other.strm")));
    }

    [Fact]
    public async Task AMusicVideoThatExistsGetsItsArtistsAndAlbum()
    {
        var library = new Mock<ILibraryManager>();
        var item = new MusicVideo { Id = Guid.NewGuid() };
        var metadata = new StubMetadata { IsProtected = false, Title = "Song", Artists = ["Singer", "Guest"], Album = "Album" };

        await new StubItemUpdater(library.Object).ApplyMetadataAsync(item, metadata, CancellationToken.None);

        Assert.Equal(["Singer", "Guest"], item.Artists);
        Assert.Equal("Album", item.Album);
    }

    [Fact]
    public async Task AnUnprotectedHomeVideoThatExistsGetsItsDate()
    {
        var library = new Mock<ILibraryManager>();
        var item = new Video { Id = Guid.NewGuid() };
        var metadata = new StubMetadata { IsProtected = false, Title = "Beach day", PremiereDate = _shot };

        await new StubItemUpdater(library.Object).ApplyMetadataAsync(item, metadata, CancellationToken.None);

        Assert.Equal(_shot.UtcDateTime, item.PremiereDate);
        Assert.False(item.IsLocked);
    }
}
