using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Audiobook stubs written end to end: a .strm per part, no .nfo, its cover beside it (D69).
public sealed class AudioBookMaterializerTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public AudioBookMaterializerTests() => _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.Books, "Libri", "books"));

    public void Dispose() => _fixture.Dispose();

    private static RemoteItem Part(string id, string name, int number, string overview = "Once upon a time", string? cover = null) => new()
    {
        Id = id,
        Name = name,
        IndexNumber = number,
        ProductionYear = 2002,
        Album = "Story",
        Artists = ["Narrator"],
        AlbumArtists = [new RemoteNamedItem("Narrator")],
        Genres = ["Fantasy"],
        Overview = overview,
        ImageTags = cover is null ? null : new Dictionary<string, string> { ["Primary"] = cover },
        Chapters = [new RemoteChapter("Chapter One", 0), new RemoteChapter("Chapter Two", 40_000_000)],
        MediaSources =
        [
            new RemoteMediaSource
            {
                Id = id,
                Container = "mp3",
                MediaStreams = [new RemoteStreamInfo("Audio", "mp3", null, null, 64_000, null, null, true) { Index = 0 }]
            }
        ]
    };

    private List<RemoteItem> Parts => _fixture.Sync.Client.Items[(TestLibraries.Books, RemoteItemType.AudioBook)];

    private async Task<RemoteServerInfo> AddAsync(params RemoteItem[] parts)
    {
        _fixture.Sync.Client.Add(TestLibraries.Books, RemoteItemType.AudioBook, parts);
        var server = await _fixture.Sync.AddServerAsync("A", TestLibraries.Books);
        await _fixture.SyncAndMaterializeAsync();
        return server;
    }

    private string BookPath(RemoteServerInfo server, params string[] parts) => _fixture.PathIn(server, TestLibraries.Books, ["Libri", "Narrator", "Story", .. parts]);

    private List<string?> FilesOf(RemoteServerInfo server) => Directory.GetFiles(BookPath(server)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

    private AudioBook Indexed(string path)
    {
        var item = new AudioBook { Id = Guid.NewGuid(), Path = path, Name = "Part" };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        return item;
    }

    [Fact]
    public async Task APartBecomesAStrmWithNoNfoThatPointsToTheLocalServer()
    {
        var a = await AddAsync(Part("p1", "One", 1), Part("p2", "Two", 2));

        Assert.Equal(["01 - One.strm", "02 - Two.strm"], FilesOf(a));
        Assert.DoesNotContain(_fixture.Files, path => path.EndsWith(".nfo", StringComparison.Ordinal));
        Assert.All(_fixture.Registry.State.Stubs, stub => Assert.Equal(StubKind.AudioBook, stub.Kind));
        Assert.StartsWith("http://127.0.0.1", StubFixture.Text(BookPath(a, "01 - One.strm")), StringComparison.Ordinal);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChangedPartReachesItsItemLockedOnlyWithProtection(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        var a = await AddAsync(Part("p1", "One", 1));
        var item = Indexed(BookPath(a, "01 - One.strm"));
        Parts[0] = Part("p1", "The start", 1, "Another plot") with { Genres = ["Saga"] };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(("The start", "Story", 1, 2002, "Another plot"), (item.Name, item.Album, item.IndexNumber, item.ProductionYear, item.Overview));
        Assert.Equal(["Narrator"], item.Artists);
        Assert.Equal(["Saga"], item.Genres);
        Assert.Equal(isProtected, item.IsLocked);
        Assert.Equal(["01 - One.strm"], FilesOf(a));
    }

    [Fact]
    public async Task ChangedChaptersReachTheItem()
    {
        var a = await AddAsync(Part("p1", "One", 1));
        var item = Indexed(BookPath(a, "01 - One.strm"));
        Parts[0] = Part("p1", "One", 1) with { Etag = "e2", Chapters = [new RemoteChapter("Only", 0)] };

        await _fixture.SyncAndMaterializeAsync();

        var saved = Assert.Single(_fixture.Chapters.Invocations, call => call.Method.Name == nameof(_fixture.Chapters.Object.SaveChapters) && call.Arguments[0] == item);
        Assert.Equal(["Only"], ((IEnumerable<ChapterInfo>)saved.Arguments[1]).Select(chapter => chapter.Name));
    }

    [Fact]
    public async Task EachPartCarriesItsOwnCoverBesideIt()
    {
        var a = await AddAsync(Part("p1", "One", 1, cover: "c1"), Part("p2", "Two", 2));

        Assert.Equal(["01 - One.jpg", "01 - One.strm", "02 - Two.strm"], FilesOf(a));
        Assert.Equal(["p1/Primary/0"], _fixture.Sync.Client.ImageReads);
    }

    [Fact]
    public async Task ANewCoverReachesThePartAndOneTheRemoteDropsLeavesWithIt()
    {
        var a = await AddAsync(Part("p1", "One", 1, cover: "c1"));
        var item = Indexed(BookPath(a, "01 - One.strm"));
        Parts[0] = Part("p1", "One", 1, cover: "c2");
        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal(BookPath(a, "01 - One.jpg"), item.GetImageInfo(ImageType.Primary, 0)?.Path);
        Parts[0] = Part("p1", "One", 1);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["01 - One.strm"], FilesOf(a));
        Assert.Empty(item.GetImages(ImageType.Primary));
    }

    // The covers of a book share one folder, and the name of a part can be the start of another's.
    [Fact]
    public async Task ThePartsNamedAlikeKeepTheirOwnCovers()
    {
        var a = await AddAsync(Part("p1", "One", 1, cover: "c1"), Part("p2", "One.b", 1, cover: "c2"));
        Assert.Equal(["01 - One.b.jpg", "01 - One.b.strm", "01 - One.jpg", "01 - One.strm"], FilesOf(a));
        Parts[0] = Part("p1", "One", 1, cover: "c3");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["01 - One.b.jpg", "01 - One.b.strm", "01 - One.jpg", "01 - One.strm"], FilesOf(a));
        Parts.RemoveAll(item => item.Id == "p1");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["01 - One.b.jpg", "01 - One.b.strm"], FilesOf(a));
    }

    // No cycles: a part of another JellyMesh read in standard mode has no version of its own, so it gets no stub.
    [Fact]
    public async Task APartThatIsTheStubOfAnotherJellyMeshGetsNoStub()
    {
        var path = "/fed/8e3f1c2a-4b5d-4e6f-8a9b-0c1d2e3f4a5b/" + TestLibraries.Books + "/Libri/Narrator/Story/01 - One.strm";
        var federated = Part("p1", "One", 1);

        var a = await AddAsync(federated with { Path = path, MediaSources = [federated.MediaSources![0] with { Path = path, Protocol = "File" }] }, Part("p2", "Two", 2));

        Assert.Equal(["02 - Two.strm"], FilesOf(a));
    }

    [Fact]
    public async Task TheLastPartsOfAnAuthorTakeTheirFolders()
    {
        var a = await AddAsync(Part("p1", "One", 1), Part("p2", "Two", 2), Part("p3", "Kept", 1) with { AlbumArtists = [new RemoteNamedItem("Other")] });
        Parts.RemoveAll(item => item.Id is "p1" or "p2");

        await _fixture.SyncAndMaterializeAsync();

        Assert.False(Directory.Exists(_fixture.PathIn(a, TestLibraries.Books, "Libri", "Narrator")));
        Assert.True(File.Exists(_fixture.PathIn(a, TestLibraries.Books, "Libri", "Other", "Story", "01 - Kept.strm")));
    }
}
