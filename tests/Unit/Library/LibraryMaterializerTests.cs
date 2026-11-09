using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class LibraryMaterializerTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static RemoteItem FilmWithArt(string id = "m", string tmdb = "1") => TestItems.Movie(id, "Film", 2020, tmdb) with
    {
        ImageTags = new Dictionary<string, string> { ["Primary"] = "p1" },
        BackdropImageTags = ["b1"],
        Overview = "Plot"
    };

    [Fact]
    public async Task AFilmBecomesStrmNfoAndImages()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            [
                Path.Combine("Film", ".jellymesh"),
                Path.Combine("Film", "Film (2020)", "Film (2020).strm"),
                Path.Combine("Film", "Film (2020)", "fanart.jpg"),
                Path.Combine("Film", "Film (2020)", "movie.nfo"),
                Path.Combine("Film", "Film (2020)", "poster.jpg"),
                Path.Combine("Serie", ".jellymesh")
            ],
            _fixture.Files);
        var stub = Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal(PlaybackRoutes.StrmUrl(8096, null, stub.Id, _fixture.Signer.Sign(stub.Id)), StubFixture.Text(_fixture.Path_("Film", "Film (2020)", "Film (2020).strm")));
        Assert.Contains("<lockdata>true</lockdata>", StubFixture.Text(_fixture.Path_("Film", "Film (2020)", "movie.nfo")), StringComparison.Ordinal);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task AServerRemovedWhileARunPlansLeavesThatRunWhole()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var server = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var before = _fixture.Files;
        var stubs = _fixture.Registry.State.Stubs.Select(stub => stub.Id).ToList();

        _fixture.AfterResolve = () => _fixture.Sync.Registry.Remove(server.Id);
        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Contains(_fixture.Hooked.LastServers ?? [], candidate => candidate.Id == server.Id);
        Assert.Equal(before, _fixture.Files);
        Assert.Equal(stubs, _fixture.Registry.State.Stubs.Select(stub => stub.Id).ToList());
    }

    [Fact]
    public async Task NoFileHoldsATokenOrARemoteAddress()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        var texts = _fixture.Files
            .Where(file => !file.EndsWith(".jpg", StringComparison.Ordinal))
            .Select(file => StubFixture.Text(Path.Combine(_fixture.Root, file)))
            .Append(StubFixture.Text(Path.Combine(Path.GetDirectoryName(_fixture.Root)!, "plugin", StubRegistry.FileName)));
        Assert.All(texts, text =>
        {
            Assert.DoesNotContain(TestValues.Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Host, text, StringComparison.Ordinal);
        });
        Assert.All(_fixture.Logger.Lines, line => Assert.DoesNotContain(TestValues.Host, line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHostileTitleStaysInsideTheFolder()
    {
        var film = TestItems.Movie("m", "../../../../etc/cron.d/evil", 2020);
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        var strm = Assert.Single(_fixture.Files, file => file.EndsWith(".strm", StringComparison.Ordinal));
        Assert.StartsWith("Film" + Path.DirectorySeparatorChar, strm, StringComparison.Ordinal);
        Assert.Equal(3, strm.Count(character => character == Path.DirectorySeparatorChar) + 1);
    }

    [Fact]
    public async Task ASecondRunChangesNothing()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var written = File.GetLastWriteTimeUtc(_fixture.Path_("Film", "Film (2020)", "movie.nfo"));
        var images = _fixture.Sync.Client.ImageReads.Count;

        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal((0, 0, 0, 0), (report.Created, report.Updated, report.Removed, report.Failed));
        Assert.Equal(images, _fixture.Sync.Client.ImageReads.Count);
        Assert.Equal(written, File.GetLastWriteTimeUtc(_fixture.Path_("Film", "Film (2020)", "movie.nfo")));
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task ARunWithNothingChangedLooksUpNoItem()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2));
        await _fixture.Sync.AddServerAsync("A", "films", "shows");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Library.Invocations.Clear();

        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal((0, 0, 0, 0), (report.Created, report.Updated, report.Removed, report.Failed));
        _fixture.Library.Verify(library => library.FindByPath(It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Fact]
    public async Task AFullReconciliationLooksUpEveryStubAgain()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var strm = _fixture.Path_("Film", "Film (2020)", "Film (2020).strm");
        var url = StubFixture.Text(strm);
        var item = new Movie { Id = Guid.NewGuid(), Path = strm, ShortcutPath = "http://127.0.0.1:8096/old" };
        _fixture.Library.Setup(library => library.FindByPath(strm, false)).Returns(item);
        using var sync = SyncWithMaterializer();

        await sync.RunIncrementalAsync(CancellationToken.None);
        Assert.Equal("http://127.0.0.1:8096/old", item.ShortcutPath);

        await sync.RunAsync(CancellationToken.None);
        Assert.Equal(url, item.ShortcutPath);
    }

    [Fact]
    public async Task NoFolderMeansNothingIsWritten()
    {
        _fixture.Sync.Store.Configuration.FederatedFolderPath = string.Empty;
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.False(report.IsFolderSet);
        Assert.Empty(_fixture.Files);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Never);
    }

    [Fact]
    public async Task ALostStrmIsWrittenAgain()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        File.Delete(_fixture.Path_("Film", "Film (2020)", "Film (2020).strm"));

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.True(File.Exists(_fixture.Path_("Film", "Film (2020)", "Film (2020).strm")));
    }

    [Fact]
    public async Task ImagesThatCouldNotBeReadAreTriedAgain()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        _fixture.Sync.Client.ImageFailure = RemoteFailure.ServerError;
        await _fixture.SyncAndMaterializeAsync();
        Assert.DoesNotContain(_fixture.Files, file => file.EndsWith(".jpg", StringComparison.Ordinal));

        _fixture.Sync.Client.ImageFailure = RemoteFailure.None;
        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Contains(_fixture.Files, file => file.EndsWith("poster.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreachableServerIsAskedForOneImageOnly()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"), FilmWithArt("b", "2") with { Name = "Other" });
        await _fixture.Sync.AddServerAsync("A", "films");
        _fixture.Sync.Client.ImageFailure = RemoteFailure.Unreachable;

        await _fixture.SyncAndMaterializeAsync();

        Assert.Single(_fixture.Sync.Client.ImageReads);
    }

    [Fact]
    public async Task ImagesAreReadAFewAtATime()
    {
        var films = Enumerable.Range(1, 6).Select(number => FilmWithArt("m" + number, number.ToString(System.Globalization.CultureInfo.InvariantCulture)) with { Name = "Film " + number }).ToArray();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, films);
        await _fixture.Sync.AddServerAsync("A", "films");
        _fixture.Sync.Client.BeforeImage = token => Task.Delay(50, token);

        await _fixture.SyncAndMaterializeAsync();

        Assert.InRange(_fixture.Sync.Client.MaxImagesInFlight, 2, 4);
        Assert.Equal(12, _fixture.Files.Count(file => file.EndsWith(".jpg", StringComparison.Ordinal)));
        Assert.All(_fixture.Registry.State.Stubs, stub => Assert.NotNull(stub.ImageTag));
    }

    [Fact]
    public async Task AChangeOnAnIndexedItemIsAppliedToTheItem()
    {
        var film = FilmWithArt();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_("Film", "Film (2020)", "Film (2020).strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film", ShortcutPath = StubFixture.Text(path) };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film with { Overview = "New plot", Genres = ["New"] };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal("New plot", item.Overview);
        Assert.Equal(["New"], item.Genres);
        Assert.True(item.IsLocked);
        Assert.Contains("New plot", StubFixture.Text(_fixture.Path_("Film", "Film (2020)", "movie.nfo")), StringComparison.Ordinal);

        // MetadataEdit would make Jellyfin rewrite the .nfo in its own format.
        _fixture.Library.Verify(library => library.UpdateItemAsync(item, It.IsAny<BaseItem>(), ItemUpdateType.MetadataImport, It.IsAny<CancellationToken>()), Times.Once);
        _fixture.Library.Verify(library => library.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangedImagesReachAnIndexedItem()
    {
        var film = FilmWithArt();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_("Film", "Film (2020)", "Film (2020).strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film" };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film with { ImageTags = new Dictionary<string, string> { ["Primary"] = "p2" } };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(_fixture.Path_("Film", "Film (2020)", "poster.jpg"), item.GetImagePath(ImageType.Primary, 0));
    }

    [Fact]
    public async Task ATitleGoneFromACompleteReadIsRemovedWithItsRow()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"), TestItems.Movie("b", "Other", 2021, "2"));
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_("Film", "Film (2020)", "Film (2020).strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].RemoveAt(0);

        var before = _fixture.Files.Length;
        await _fixture.SyncAndMaterializeAsync();

        Assert.False(Directory.Exists(_fixture.Path_("Film", "Film (2020)")));
        Assert.True(_fixture.Files.Length < before);
        Assert.Single(_fixture.Registry.State.Stubs);
        _fixture.Library.Verify(library => library.DeleteItem(item, It.Is<DeleteOptions>(options => !options.DeleteFileLocation)), Times.Once);
    }

    [Fact]
    public async Task RemovingAServerDeletesWhatOnlyItHadAndMovesTheRestToTheOther()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("extra", "Extra", "movies"));
        _fixture.Sync.Client.Add("extra", RemoteItemType.Movie, TestItems.Movie("b", "Other", 2021, "2"));
        var removed = await _fixture.Sync.AddServerAsync("A", "films", "extra");
        var kept = await _fixture.Sync.AddServerAsync("B", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_("Film", "Other (2021)", "Other (2021).strm");
        Assert.True(File.Exists(path));
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        using var sync = SyncWithMaterializer();

        await sync.MarkRemovedAsync(removed.Id, CancellationToken.None);
        _fixture.Sync.Registry.Remove(removed.Id);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_fixture.Path_("Film", "Other (2021)")));
        Assert.True(File.Exists(_fixture.Path_("Film", "Film (2020)", "Film (2020).strm")));
        Assert.Equal(kept.Id, Assert.Single(Assert.Single(_fixture.Registry.State.Stubs).Sources).ServerId);
        Assert.Null(_fixture.Sync.Index.Snapshot.Find(removed.Id));
        _fixture.Library.Verify(library => library.DeleteItem(item, It.Is<DeleteOptions>(options => !options.DeleteFileLocation)), Times.Once);
    }

    [Fact]
    public async Task RemovingAServerDeletesItsContentWhileTheKeyIsBroken()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var removed = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        using var sync = SyncWithMaterializer();

        _fixture.Sync.KeySource.Key = null;
        await sync.MarkRemovedAsync(removed.Id, CancellationToken.None);
        _fixture.Sync.Registry.Remove(removed.Id);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_fixture.Path_("Film", "Film (2020)")));
        Assert.Empty(_fixture.Registry.State.Stubs);
    }

    [Fact]
    public async Task AServerMissingFromAResetConfigurationKeepsItsStubsAndCatalog()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var server = await _fixture.Sync.AddServerAsync("A", "films");
        using var sync = SyncWithMaterializer();
        await sync.RunAsync(CancellationToken.None);
        var before = _fixture.Files;

        _fixture.Sync.Registry.Remove(server.Id);
        await sync.RunAsync(CancellationToken.None);

        Assert.Equal(before, _fixture.Files);
        Assert.Single(_fixture.Registry.State.Stubs);
        Assert.NotNull(_fixture.Sync.Index.Snapshot.Find(server.Id));
        Assert.Equal(1, sync.GetStatus().UnconfiguredStubs);
    }

    [Fact]
    public async Task ARemovalIsRememberedAcrossARestart()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var server = await _fixture.Sync.AddServerAsync("A", "films");
        using var sync = SyncWithMaterializer();
        await sync.RunAsync(CancellationToken.None);

        await sync.MarkRemovedAsync(server.Id, CancellationToken.None);
        using var restarted = new StubRegistry(Path.Combine(Path.GetDirectoryName(_fixture.Root)!, "plugin"), _fixture.Sync.Store, new CapturingLogger<StubRegistry>());

        Assert.Equal([server.Id], restarted.State.RemovedServers);
    }

    [Fact]
    public async Task AFailedSyncRemovesNothing()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].Clear();
        _fixture.Sync.Client.OnPage = (_, _) => RemoteFailure.Unreachable;

        await _fixture.SyncAndMaterializeAsync();

        Assert.True(File.Exists(_fixture.Path_("Film", "Film (2020)", "Film (2020).strm")));
    }

    [Fact]
    public async Task ACompleteReadOfAnEmptyLibraryHoldsTheStubsAndSaysSo()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].Clear();

        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.True(File.Exists(_fixture.Path_("Film", "Film (2020)", "Film (2020).strm")));
        Assert.Equal(1, Assert.Single(report.Held).Stubs);
        Assert.Contains(_fixture.Logger.Lines, line => line.Contains("came back empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALocalCopyDoesNotRemoveTheStubHere()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = Guid.NewGuid().ToString("N"), Kind = ContentKind.Movie, Ids = new ContentIds("1") });

        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.True(File.Exists(_fixture.Path_("Film", "Film (2020)", "Film (2020).strm")));
    }

    private async Task<(string Path, string Signed, Movie Item)> AnIndexedStubHoldingAsync(Func<string, string> url)
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_("Film", "Film (2020)", "Film (2020).strm");
        var signed = StubFixture.Text(path);
        var old = url(signed);
        var written = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, old);

        // A write by someone else comes later than the plugin's; file times can be too coarse to show it by themselves.
        File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Renamed by a fetcher", ShortcutPath = old };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        return (path, signed, item);
    }

    [Theory]
    [InlineData("?sig=")]
    [InlineData("?sig=0000")]
    [InlineData("")]
    public async Task AStubWithoutAValidSignatureIsSignedInPlace(string query)
    {
        var (path, signed, item) = await AnIndexedStubHoldingAsync(url => url.Split('?')[0] + query);
        var nfo = _fixture.Path_("Film", "Film (2020)", "movie.nfo");
        var nfoWritten = File.GetLastWriteTimeUtc(nfo);
        var stub = Assert.Single(_fixture.Registry.State.Stubs);

        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(signed, StubFixture.Text(path));
        Assert.Equal(signed, item.ShortcutPath);
        Assert.Equal((0, 1, 0, 0), (report.Created, report.Updated, report.Removed, report.Failed));

        // The stub, its path and hence the item stay, and nothing but the URL is touched.
        var after = Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal((stub.Id, stub.RelativePath), (after.Id, after.RelativePath));
        Assert.Equal("Renamed by a fetcher", item.Name);
        Assert.Equal(nfoWritten, File.GetLastWriteTimeUtc(nfo));
        _fixture.Library.Verify(library => library.UpdateItemAsync(item, It.IsAny<BaseItem>(), ItemUpdateType.MetadataImport, It.IsAny<CancellationToken>()), Times.Once);
        _fixture.Library.Verify(library => library.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task AnItemLeftOnAnOldUrlIsMovedEvenWithTheFileRight()
    {
        var (path, signed, item) = await AnIndexedStubHoldingAsync(url => url);
        item.ShortcutPath = signed.Split('?')[0];

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(signed, StubFixture.Text(path));
        Assert.Equal(signed, item.ShortcutPath);
    }

    [Fact]
    public async Task AnEntryFromAnOlderVersionGetsItsItemMovedOnce()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_("Film", "Film (2020)", "Film (2020).strm");
        var signed = StubFixture.Text(path);
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film", ShortcutPath = signed.Split('?')[0] };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        var state = _fixture.Registry.State;
        await _fixture.Registry.SaveAsync(state with { Stubs = state.Stubs.Select(stub => stub with { UrlTag = null, StrmWrittenUtc = null }).ToList() }, CancellationToken.None);

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);
        _fixture.Library.Invocations.Clear();
        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(signed, item.ShortcutPath);
        _fixture.Library.Verify(library => library.FindByPath(It.IsAny<string>(), It.IsAny<bool?>()), Times.Never);
    }

    [Fact]
    public async Task ASignedStubIsLeftAlone()
    {
        var (path, _, item) = await AnIndexedStubHoldingAsync(url => url);
        var written = File.GetLastWriteTimeUtc(path);

        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(0, report.Updated);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        _fixture.Library.Verify(library => library.UpdateItemAsync(item, It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AStubWaitingForThePostScanTaskIsSignedToo()
    {
        var (path, signed, item) = await AnIndexedStubHoldingAsync(url => url.Split('?')[0]);
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = Guid.NewGuid().ToString("N"), Kind = ContentKind.Movie, Ids = new ContentIds("1") });

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(signed, StubFixture.Text(path));
        Assert.Equal(signed, item.ShortcutPath);
    }

    [Fact]
    public async Task AStrmHoldsNothingButTheEndpointAndTheLocalSignature()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        var text = StubFixture.Text(_fixture.Path_("Film", "Film (2020)", "Film (2020).strm"));
        var stub = Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal("http://127.0.0.1:8096/JellyMesh/Play/" + stub.Id + "?sig=" + _fixture.Signer.Sign(stub.Id), text);
        Assert.True(PlaybackRoutes.IsPlaybackEndpoint(text));
        Assert.True(PlaybackRoutes.TryGetStubId(text, out var parsed));
        Assert.Equal(stub.Id, parsed);
    }

    [Fact]
    public async Task ASeriesGetsItsFoldersAndLockedFields()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2));
        await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        var tvshow = StubFixture.Text(_fixture.Path_("Serie", "Show (2020)", "tvshow.nfo"));
        Assert.Contains("<lockedfields>", tvshow, StringComparison.Ordinal);
        Assert.DoesNotContain("<lockdata>", tvshow, StringComparison.Ordinal);
        var episode = StubFixture.Text(_fixture.Path_("Serie", "Show (2020)", "Season 01", "Show S01E02.nfo"));
        Assert.Contains("<lockdata>true</lockdata>", episode, StringComparison.Ordinal);
        Assert.True(Assert.Single(_fixture.Registry.State.Series).NeedsRefresh);
    }

    [Fact]
    public async Task ADoubleEpisodeGetsOneStubWithItsRange()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2) with { IndexNumberEnd = 3 });
        await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        var stub = Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal((2, 3), (stub.EpisodeNumber, stub.EpisodeNumberEnd));
        Assert.Equal(["e"], stub.Sources.Select(source => source.ItemId));
        var nfo = StubFixture.Text(_fixture.Path_("Serie", "Show (2020)", "Season 01", "Show S01E02-E03.nfo"));
        Assert.Contains("<episode>2</episode>", nfo, StringComparison.Ordinal);
        Assert.Contains("<episodenumberend>3</episodenumberend>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStubSeriesTakesTheKeyOfTheLocalSeries()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2));
        await _fixture.Sync.AddServerAsync("A", "shows");
        var localSeries = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Show Exact",
            Overview = "Local plot",
            ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "71" }
        };
        var localEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = localSeries.Id };
        _fixture.Library.Setup(library => library.GetItemById(localEpisode.Id)).Returns(localEpisode);
        _fixture.Library.Setup(library => library.GetItemById(localSeries.Id)).Returns(localSeries);
        _fixture.LocalCatalog.Items.Add(new CatalogItem
        {
            ServerId = "local",
            ItemId = localEpisode.Id.ToString("N"),
            Kind = ContentKind.Episode,
            Title = "Show Exact",
            Ids = new ContentIds(null, null, "71"),
            SeasonNumber = 1,
            EpisodeNumber = 1
        });

        await _fixture.SyncAndMaterializeAsync();

        var folder = Assert.Single(_fixture.Registry.State.Series).RelativePath;
        var tvshow = StubFixture.Text(Path.Combine(_fixture.Root, folder, "tvshow.nfo"));
        Assert.Contains("<title>Show Exact</title>", tvshow, StringComparison.Ordinal);
        Assert.Contains("<plot>Local plot</plot>", tvshow, StringComparison.Ordinal);
        Assert.Contains("<uniqueid type=\"tvdb\">71</uniqueid>", tvshow, StringComparison.Ordinal);
        Assert.DoesNotContain("tt71", tvshow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASyncMaterializesAndShowsWhatIsHeld()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        using var sync = new CatalogSyncService(
            _fixture.Sync.Registry,
            _fixture.Sync.Reader,
            _fixture.Sync.Index,
            _fixture.Sync.Vault,
            _fixture.Sync.Clock,
            _fixture.Sync.Logger,
            _fixture.Materializer);

        await sync.RunAsync(CancellationToken.None);
        Assert.Single(_fixture.Registry.State.Stubs);

        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].Clear();
        await sync.RunAsync(CancellationToken.None);

        Assert.Single(sync.GetStatus().HeldLibraries);
    }

    [Fact]
    public async Task AFailureInMaterializationDoesNotFailTheSync()
    {
        var materializer = new Mock<ILibraryMaterializer>();
        materializer.Setup(item => item.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException(TestValues.Host));
        await _fixture.Sync.AddServerAsync("A", "films");
        using var sync = new CatalogSyncService(
            _fixture.Sync.Registry,
            _fixture.Sync.Reader,
            _fixture.Sync.Index,
            _fixture.Sync.Vault,
            _fixture.Sync.Clock,
            _fixture.Sync.Logger,
            materializer.Object);

        Assert.Equal(SyncStartResult.Started, await sync.RunAsync(CancellationToken.None));

        Assert.False(sync.GetStatus().IsRunning);
        Assert.DoesNotContain(_fixture.Sync.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
    }

    private CatalogSyncService SyncWithMaterializer() => new(
        _fixture.Sync.Registry,
        _fixture.Sync.Reader,
        _fixture.Sync.Index,
        _fixture.Sync.Vault,
        _fixture.Sync.Clock,
        _fixture.Sync.Logger,
        _fixture.Materializer,
        _fixture.Registry);
}
