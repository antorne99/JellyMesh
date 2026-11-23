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
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The stub providers on an audiobook part: 12.4 does not store IsShortcut on an AudioBook, so it is set on every refresh (F48),
// and reads no tag from its stub, so every field and the chapters come from the plugin (D69).
public sealed class AudioBookProviderTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private StubBeforeProbeProvider Before() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private StubAfterProbeProvider After() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private static MetadataRefreshOptions Options() => new(new Mock<IDirectoryService>().Object) { EnableRemoteContentProbe = true };

    private static string WriteStrm(string path, string url)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, url + "\n");
        return path;
    }

    private async Task<string> PartStubAsync()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.Books, "Libri", "books"));
        _fixture.Sync.Client.Add(TestLibraries.Books, RemoteItemType.AudioBook, new RemoteItem
        {
            Id = "p1",
            Name = "The start",
            IndexNumber = 3,
            ProductionYear = 2002,
            Album = "Story",
            Artists = ["Narrator", "Guest"],
            AlbumArtists = [new RemoteNamedItem("Narrator")],
            Genres = ["Fantasy"],
            Overview = "Once upon a time",
            ProviderIds = new Dictionary<string, string?> { ["Audible"] = "B00" },
            Chapters = [new RemoteChapter("Chapter One", 0), new RemoteChapter("Chapter Two", 40_000_000)],
            MediaSources =
            [
                new RemoteMediaSource
                {
                    Id = "p1",
                    Container = "m4b",
                    Bitrate = 64_000,
                    RunTimeTicks = 120_000_000,
                    MediaStreams = [new RemoteStreamInfo("Audio", "aac", null, null, 64_000, null, null, true) { Index = 0, Channels = 1 }]
                }
            ]
        });
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.Books);
        await _fixture.SyncAndMaterializeAsync();
        return _fixture.PathIn(a, TestLibraries.Books, "Libri", "Narrator", "Story", "03 - The start.strm");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewPartGetsItsFieldsAndPlotFromTheIndexAndIsLockedOnlyWithProtection(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        var item = new AudioBook { Id = Guid.NewGuid(), Path = await PartStubAsync(), Name = "03 - The start" };

        var result = await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.Equal(("The start", "Story", 3, 2002, "Once upon a time"), (item.Name, item.Album, item.IndexNumber, item.ProductionYear, item.Overview));
        Assert.Equal(["Narrator", "Guest"], item.Artists);
        Assert.Equal(["Narrator"], item.AlbumArtists);
        Assert.Equal(["Fantasy"], item.Genres);
        Assert.Equal("B00", item.ProviderIds["Audible"]);
        Assert.Equal(isProtected, item.IsLocked);
        Assert.Equal((120_000_000L, "m4b"), (item.RunTimeTicks, item.Container));
    }

    [Fact]
    public async Task AnUnchangedPartIsNotSavedAgainAndAPlotChangeIs()
    {
        var item = new AudioBook { Id = Guid.NewGuid(), Path = await PartStubAsync() };
        await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, await Before().FetchAsync(item, Options(), CancellationToken.None));
        item.Overview = null;
        Assert.Equal(ItemUpdateType.MetadataImport, await Before().FetchAsync(item, Options(), CancellationToken.None));
        Assert.Equal("Once upon a time", item.Overview);
    }

    // Stock Jellyfin stores an empty chapter list after its probe, so the second provider writes the remote's back.
    [Fact]
    public async Task TheSecondProviderWritesTheRemoteChaptersAndStream()
    {
        var item = new AudioBook { Id = Guid.NewGuid(), Path = await PartStubAsync() };

        await After().FetchAsync(item, Options(), CancellationToken.None);

        var saved = Assert.Single(_fixture.Chapters.Invocations, call => call.Method.Name == nameof(_fixture.Chapters.Object.SaveChapters) && call.Arguments[0] == item);
        Assert.Equal(
            [("Chapter One", 0L), ("Chapter Two", 40_000_000L)],
            ((IEnumerable<ChapterInfo>)saved.Arguments[1]).Select(chapter => (chapter.Name, chapter.StartPositionTicks)));
        _fixture.Streams.Verify(streams => streams.SaveMediaStreams(item.Id, It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ThePlotOfATrackIsLeftAlone()
    {
        var track = new Audio { Name = "Song", Overview = "Liner notes" };

        StubItemUpdater.WriteTrack(track, new StubMetadata { IsProtected = false, Title = "Song" });

        Assert.Equal("Liner notes", track.Overview);
    }

    [Fact]
    public void TheyRunOnAudiobooks()
    {
        Assert.IsAssignableFrom<ICustomMetadataProvider<AudioBook>>(Before());
        Assert.IsAssignableFrom<ICustomMetadataProvider<AudioBook>>(After());
    }

    [Fact]
    public async Task AnAudiobookStrmInTheFolderIsAShortcutToItsUrlAndIsNotProbed()
    {
        var path = WriteStrm(_fixture.Path_("Libri", "Book", "01 - Part.strm"), "http://127.0.0.1:8096/JellyMesh/Strm/abc?sig=x");
        var item = new AudioBook { Id = Guid.NewGuid(), Path = path, IsLocked = true };
        var options = Options();

        await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.False(options.EnableRemoteContentProbe);
        Assert.True(item.IsShortcut);
        Assert.Equal("http://127.0.0.1:8096/JellyMesh/Strm/abc?sig=x", item.ShortcutPath);
    }

    [Fact]
    public async Task AnAudiobookOutsideTheFolderIsLeftAlone()
    {
        var path = WriteStrm(Path.Combine(_fixture.Root + "-elsewhere", "Book.strm"), "http://example.invalid/book");
        var item = new AudioBook { Id = Guid.NewGuid(), Path = path };
        var options = Options();

        await Before().FetchAsync(item, options, CancellationToken.None);

        Assert.True(options.EnableRemoteContentProbe);
        Assert.False(item.IsShortcut);
        Assert.Null(item.ShortcutPath);
    }
}
