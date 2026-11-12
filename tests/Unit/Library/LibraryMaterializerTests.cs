using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
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
    private const string RemoteServerId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";

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
        var a = await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            new[]
            {
                StubFixture.In(a, "Film", ".jellymesh"),
                StubFixture.In(a, "Film", "Film (2020)", "Film (2020) - A.strm"),
                StubFixture.In(a, "Film", "Film (2020)", "fanart.jpg"),
                StubFixture.In(a, "Film", "Film (2020)", "movie.nfo"),
                StubFixture.In(a, "Film", "Film (2020)", "poster.jpg"),
                StubFixture.In(a, "Serie", ".jellymesh"),
                Path.Combine("Film", ".jellymesh"),
                Path.Combine("Serie", ".jellymesh")
            }.Order(StringComparer.Ordinal),
            _fixture.Files);
        var stub = Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal(a.Id, stub.ServerId);
        Assert.Equal(PlaybackRoutes.StrmUrl(8096, null, stub.Id, _fixture.Signer.Sign(stub.Id)), StubFixture.Text(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm")));
        Assert.Contains("<lockdata>true</lockdata>", StubFixture.Text(_fixture.Path_(a, "Film", "Film (2020)", "movie.nfo")), StringComparison.Ordinal);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task AServerWithNothingSelectedStillGetsItsFolders()
    {
        var a = await _fixture.Sync.AddServerAsync("A");
        var b = await _fixture.Sync.AddServerAsync("B", "films");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            new[]
            {
                StubFixture.In(a, "Film", ".jellymesh"),
                StubFixture.In(a, "Serie", ".jellymesh"),
                StubFixture.In(b, "Film", ".jellymesh"),
                StubFixture.In(b, "Serie", ".jellymesh"),
                Path.Combine("Film", ".jellymesh"),
                Path.Combine("Serie", ".jellymesh")
            }.Order(StringComparer.Ordinal),
            _fixture.Files);
    }

    [Fact]
    public async Task TheFoldersOfAServerCanBeWrittenBeforeAnySync()
    {
        var a = await _fixture.Sync.AddServerAsync("A");

        _fixture.Materializer.PrepareServerFolders();

        Assert.Equal(
            new[]
            {
                StubFixture.In(a, "Film", ".jellymesh"),
                StubFixture.In(a, "Serie", ".jellymesh"),
                Path.Combine("Film", ".jellymesh"),
                Path.Combine("Serie", ".jellymesh")
            }.Order(StringComparer.Ordinal),
            _fixture.Files);
    }

    [Fact]
    public async Task WithoutAFederatedFolderNoServerFolderIsWritten()
    {
        await _fixture.Sync.AddServerAsync("A");
        _fixture.Sync.Store.Configuration.FederatedFolderPath = string.Empty;

        _fixture.Materializer.PrepareServerFolders();

        Assert.Empty(_fixture.Files);
    }

    [Fact]
    public async Task AFederatedFolderThatCannotBeWrittenIsLoggedAndNotThrown()
    {
        await _fixture.Sync.AddServerAsync("A");
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.Root)!);
        await File.WriteAllTextAsync(_fixture.Root, "a file where the folder should be");

        _fixture.Materializer.PrepareServerFolders();

        Assert.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, _fixture.Logger.Levels);
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
        var a = await _fixture.Sync.AddServerAsync("A", "films");

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
        var a = await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        var strm = Assert.Single(_fixture.Files, file => file.EndsWith(".strm", StringComparison.Ordinal));
        Assert.StartsWith(StubFixture.In(a, "Film") + Path.DirectorySeparatorChar, strm, StringComparison.Ordinal);
        Assert.Equal(4, strm.Count(character => character == Path.DirectorySeparatorChar) + 1);
    }

    [Fact]
    public async Task ASecondRunChangesNothing()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var written = File.GetLastWriteTimeUtc(_fixture.Path_(a, "Film", "Film (2020)", "movie.nfo"));
        var images = _fixture.Sync.Client.ImageReads.Count;

        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal((0, 0, 0, 0), (report.Created, report.Updated, report.Removed, report.Failed));
        Assert.Equal(images, _fixture.Sync.Client.ImageReads.Count);
        Assert.Equal(written, File.GetLastWriteTimeUtc(_fixture.Path_(a, "Film", "Film (2020)", "movie.nfo")));
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task ARunWithNothingChangedLooksUpNoItem()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2));
        var a = await _fixture.Sync.AddServerAsync("A", "films", "shows");
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
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var strm = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
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
        var a = await _fixture.Sync.AddServerAsync("A", "films");

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
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        File.Delete(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm"));

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.True(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm")));
    }

    [Fact]
    public async Task ImagesThatCouldNotBeReadAreTriedAgain()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var a = await _fixture.Sync.AddServerAsync("A", "films");
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
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        _fixture.Sync.Client.ImageFailure = RemoteFailure.Unreachable;

        await _fixture.SyncAndMaterializeAsync();

        Assert.Single(_fixture.Sync.Client.ImageReads);
    }

    [Fact]
    public async Task ImagesAreReadAFewAtATime()
    {
        var films = Enumerable.Range(1, 6).Select(number => FilmWithArt("m" + number, number.ToString(System.Globalization.CultureInfo.InvariantCulture)) with { Name = "Film " + number }).ToArray();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, films);
        var a = await _fixture.Sync.AddServerAsync("A", "films");
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
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film", ShortcutPath = StubFixture.Text(path) };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film with { Overview = "New plot", Genres = ["New"] };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal("New plot", item.Overview);
        Assert.Equal(["New"], item.Genres);
        Assert.True(item.IsLocked);
        Assert.Contains("New plot", StubFixture.Text(_fixture.Path_(a, "Film", "Film (2020)", "movie.nfo")), StringComparison.Ordinal);

        // MetadataEdit would make Jellyfin rewrite the .nfo in its own format.
        _fixture.Library.Verify(library => library.UpdateItemAsync(item, It.IsAny<BaseItem>(), ItemUpdateType.MetadataImport, It.IsAny<CancellationToken>()), Times.Once);
        _fixture.Library.Verify(library => library.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), ItemUpdateType.MetadataEdit, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ANewTrailerReachesAnIndexedItemAndAGoneOneLeavesIt()
    {
        var film = FilmWithArt();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film", ShortcutPath = StubFixture.Text(path) };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film with { RemoteTrailers = [new RemoteMediaUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ")] };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["https://www.youtube.com/watch?v=dQw4w9WgXcQ"], item.RemoteTrailers.Select(trailer => trailer.Url));
        Assert.Contains("plugin://plugin.video.youtube/play/?video_id=dQw4w9WgXcQ", StubFixture.Text(_fixture.Path_(a, "Film", "Film (2020)", "movie.nfo")), StringComparison.Ordinal);

        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film;
        await _fixture.SyncAndMaterializeAsync();

        Assert.Empty(item.RemoteTrailers);
    }

    [Fact]
    public async Task ANewRuntimeReachesAnIndexedItem()
    {
        var film = FilmWithArt() with { RunTimeTicks = 100, Etag = "1" };
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film" };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film with { RunTimeTicks = 72_000_000_000, Etag = "2" };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(72_000_000_000, item.RunTimeTicks);
        _fixture.Library.Verify(library => library.UpdateItemAsync(item, It.IsAny<BaseItem>(), ItemUpdateType.MetadataImport, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ChangedImagesReachAnIndexedItem()
    {
        var film = FilmWithArt();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film" };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film with { ImageTags = new Dictionary<string, string> { ["Primary"] = "p2" } };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(_fixture.Path_(a, "Film", "Film (2020)", "poster.jpg"), item.GetImagePath(ImageType.Primary, 0));
    }

    [Fact]
    public async Task ATitleGoneFromACompleteReadIsRemovedWithItsRow()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"), TestItems.Movie("b", "Other", 2021, "2"));
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].RemoveAt(0);

        var before = _fixture.Files.Length;
        await _fixture.SyncAndMaterializeAsync();

        Assert.False(Directory.Exists(_fixture.Path_(a, "Film", "Film (2020)")));
        Assert.True(_fixture.Files.Length < before);
        Assert.Single(_fixture.Registry.State.Stubs);
        _fixture.Library.Verify(library => library.DeleteItem(item, It.Is<DeleteOptions>(options => !options.DeleteFileLocation)), Times.Once);
    }

    // Decision 48: the sync history shows what each server's folder got.
    [Fact]
    public async Task TheReportCountsTheStubsOfEachServer()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"), TestItems.Movie("b", "Other", 2021, "2"));
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        var b = await _fixture.Sync.AddServerAsync("B", "films");
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);

        var first = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(new StubCounts(2, 0, 0, 0), first.ByServer[a.Id]);
        Assert.Equal(new StubCounts(2, 0, 0, 0), first.ByServer[b.Id]);

        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].RemoveAt(0);
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);

        var second = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(new StubCounts(0, 0, 1, 0), second.ByServer[a.Id]);
        Assert.Equal(new StubCounts(0, 0, 1, 0), second.ByServer[b.Id]);
        Assert.Equal(2, second.Removed);
    }

    [Fact]
    public async Task RemovingAServerDeletesItsStubsAndLeavesThoseOfTheOther()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("extra", "Extra", "movies"));
        _fixture.Sync.Client.Add("extra", RemoteItemType.Movie, TestItems.Movie("b", "Other", 2021, "2"));
        var removed = await _fixture.Sync.AddServerAsync("A", "films", "extra");
        var kept = await _fixture.Sync.AddServerAsync("B", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(removed, "Film", "Other (2021)", "Other (2021) - A.strm");
        Assert.True(File.Exists(path));
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        using var sync = SyncWithMaterializer();

        await sync.RemoveServerAsync(removed.Id, () => Task.FromResult(_fixture.Sync.Registry.Remove(removed.Id)), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_fixture.Path_(removed, "Film", "Other (2021)")));
        Assert.False(Directory.Exists(_fixture.Path_(removed, "Film", "Film (2020)")));
        Assert.True(File.Exists(_fixture.Path_(removed, "Film", ".jellymesh")));
        Assert.True(File.Exists(_fixture.Path_(kept, "Film", "Film (2020)", "Film (2020) - B.strm")));
        Assert.Equal(kept.Id, Assert.Single(_fixture.Registry.State.Stubs).ServerId);
        Assert.Null(_fixture.Sync.Index.Snapshot.Find(removed.Id));
        _fixture.Library.Verify(library => library.DeleteItem(item, It.Is<DeleteOptions>(options => !options.DeleteFileLocation)), Times.Once);
    }

    // Decisions 116 and 117: a removed remote added again is known by its Server Id and gets its folder and label back.
    [Fact]
    public async Task AServerAddedBackTakesBackItsFolderItsLabelAndItsStubs()
    {
        _fixture.Sync.Client.ServerId = RemoteServerId;
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var first = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(first, "Film", "Film (2020)", "Film (2020) - A.strm");
        Assert.True(File.Exists(path));
        using var sync = SyncWithMaterializer();
        await sync.RemoveServerAsync(first.Id, () => Task.FromResult(_fixture.Sync.Registry.Remove(first.Id)), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        Assert.False(File.Exists(path));

        var again = await _fixture.Sync.AddServerAsync("Renamed", "films");
        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(first.Id, again.Id);
        Assert.True(File.Exists(path));
        Assert.Empty(_fixture.Registry.State.RemovedServers);
        Assert.Equal(first.Id, Assert.Single(_fixture.Registry.State.Stubs).ServerId);
    }

    [Fact]
    public async Task AServerAddedBackBeforeTheCleanupKeepsItsStubs()
    {
        _fixture.Sync.Client.ServerId = RemoteServerId;
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var first = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(first, "Film", "Film (2020)", "Film (2020) - A.strm");
        using var sync = SyncWithMaterializer();
        await sync.RemoveServerAsync(first.Id, () => Task.FromResult(_fixture.Sync.Registry.Remove(first.Id)), CancellationToken.None);

        await _fixture.Sync.AddServerAsync("A", "films");
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.NotNull(_fixture.Sync.Index.Snapshot.Find(first.Id));
        Assert.Empty(_fixture.Registry.State.RemovedServers);
        Assert.Equal(first.Id, Assert.Single(_fixture.Registry.State.Stubs).ServerId);
    }

    [Fact]
    public async Task AServerAddedBackAfterItsFolderWasDeletedWritesItAgain()
    {
        _fixture.Sync.Client.ServerId = RemoteServerId;
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var first = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        using var sync = SyncWithMaterializer();
        await sync.RemoveServerAsync(first.Id, () => Task.FromResult(_fixture.Sync.Registry.Remove(first.Id)), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        Directory.Delete(_fixture.Path_(first), true);

        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();

        Assert.True(File.Exists(_fixture.Path_(first, "Film", "Film (2020)", "Film (2020) - A.strm")));
        Assert.Empty(_fixture.Registry.State.RemovedServers);
    }

    [Fact]
    public async Task AnotherServerNeverTakesTheLabelOfARemovedOne()
    {
        _fixture.Sync.Client.ServerId = RemoteServerId;
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var first = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        using var sync = SyncWithMaterializer();
        await sync.RemoveServerAsync(first.Id, () => Task.FromResult(_fixture.Sync.Registry.Remove(first.Id)), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        _fixture.Sync.Client.ServerId = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";

        var other = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();

        Assert.NotEqual(first.Id, other.Id);
        var stub = Path.GetFileName(Assert.Single(Directory.GetFiles(_fixture.Path_(other, "Film", "Film (2020)"), "*.strm")));
        Assert.StartsWith("Film (2020) - A ", stub, StringComparison.Ordinal);
        Assert.Equal("A", _fixture.Registry.State.Labels[first.Id]);
    }

    [Fact]
    public async Task RemovingAServerDeletesItsContentWhileTheKeyIsBroken()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var removed = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        using var sync = SyncWithMaterializer();

        _fixture.Sync.KeySource.Key = null;
        await sync.RemoveServerAsync(removed.Id, () => Task.FromResult(_fixture.Sync.Registry.Remove(removed.Id)), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_fixture.Path_(removed, "Film", "Film (2020)")));
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

        await sync.RemoveServerAsync(server.Id, () => Task.FromResult(true), CancellationToken.None);
        using var restarted = new StubRegistry(Path.Combine(Path.GetDirectoryName(_fixture.Root)!, "plugin"), _fixture.Sync.Store, new CapturingLogger<StubRegistry>());

        Assert.Equal([server.Id], restarted.State.RemovedServers);
    }

    [Fact]
    public async Task AFailedSyncRemovesNothing()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].Clear();
        _fixture.Sync.Client.OnPage = (_, _) => RemoteFailure.Unreachable;

        await _fixture.SyncAndMaterializeAsync();

        Assert.True(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm")));
    }

    [Fact]
    public async Task ACompleteReadOfAnEmptyLibraryHoldsTheStubsAndSaysSo()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].Clear();

        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.True(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm")));
        Assert.Equal(1, Assert.Single(report.Held).Stubs);
        Assert.Contains(_fixture.Logger.Lines, line => line.Contains("came back empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALocalCopyDoesNotRemoveTheStubHere()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = Guid.NewGuid().ToString("N"), Kind = ContentKind.Movie, Ids = new ContentIds("1") });

        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(0, report.Removed);
        Assert.True(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm")));
    }

    [Fact]
    public async Task ATitleWithALocalCopyGetsAStubOfEachServerInOneGroup()
    {
        var local = Guid.NewGuid();
        _fixture.LocalCatalog.Items.Add(new CatalogItem { ServerId = "local", ItemId = local.ToString("N"), Kind = ContentKind.Movie, Ids = new ContentIds("1") });
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("films-b", "Film B", "movies"));
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        _fixture.Sync.Client.Add("films-b", RemoteItemType.Movie, FilmWithArt("mb"));
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        var b = await _fixture.Sync.AddServerAsync("B", "films-b");

        await _fixture.SyncAndMaterializeAsync();

        Assert.True(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm")));
        Assert.True(File.Exists(_fixture.Path_(b, "Film", "Film (2020)", "Film (2020) - B.strm")));
        var group = Assert.Single(_fixture.Registry.State.Groups);
        Assert.Equal(_fixture.Registry.State.Stubs.Select(stub => stub.Id).Order(StringComparer.Ordinal), group.StubIds.Order(StringComparer.Ordinal));
        Assert.Equal([local], group.LocalItemIds);
    }

    private async Task<(string Path, string Signed, Movie Item)> AnIndexedStubHoldingAsync(Func<string, string> url)
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
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
        var nfo = Path.Combine(Path.GetDirectoryName(path)!, "movie.nfo");
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
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
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
    public async Task AStubOfTheFlatLayoutIsLeftInPlaceAndSigned()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var own = Assert.Single(_fixture.Registry.State.Stubs);
        var flat = new StubEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = StubKind.Movie,
            RelativePath = Path.Combine("Film", "Film (2020)", "Film (2020).strm"),
            Sources = own.Sources,
            IdentityKey = own.IdentityKey
        };
        var path = _fixture.Path_(flat.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "http://127.0.0.1:8096/JellyMesh/Play/" + flat.Id);
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        var state = _fixture.Registry.State;
        await _fixture.Registry.SaveAsync(state with { Stubs = [.. state.Stubs, flat] }, CancellationToken.None);

        await _fixture.SyncAndMaterializeAsync();

        var signed = PlaybackRoutes.StrmUrl(8096, null, flat.Id, _fixture.Signer.Sign(flat.Id));
        Assert.Equal(signed, StubFixture.Text(path));
        Assert.Equal(signed, item.ShortcutPath);
        Assert.Equal(flat, _fixture.Registry.State.Stubs.Single(stub => stub.Id == flat.Id) with { UrlTag = null, StrmWrittenUtc = null });
        Assert.Equal(2, _fixture.Registry.State.Stubs.Count);
    }

    [Fact]
    public async Task AStrmHoldsNothingButTheEndpointAndTheLocalSignature()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var a = await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        var text = StubFixture.Text(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm"));
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
        var a = await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        var tvshow = StubFixture.Text(_fixture.Path_(a, "Serie", "Show (2020)", "tvshow.nfo"));
        Assert.Contains("<lockedfields>", tvshow, StringComparison.Ordinal);
        Assert.DoesNotContain("<lockdata>", tvshow, StringComparison.Ordinal);
        var episode = StubFixture.Text(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "Show S01E02 - A.nfo"));
        Assert.Contains("<lockdata>true</lockdata>", episode, StringComparison.Ordinal);
        Assert.True(Assert.Single(_fixture.Registry.State.Series).NeedsRefresh);
    }

    [Fact]
    public async Task ADoubleEpisodeGetsOneStubWithItsRange()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2) with { IndexNumberEnd = 3 });
        var a = await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        var stub = Assert.Single(_fixture.Registry.State.Stubs);
        Assert.Equal((2, 3), (stub.EpisodeNumber, stub.EpisodeNumberEnd));
        Assert.Equal(["e"], stub.Sources.Select(source => source.ItemId));
        var nfo = StubFixture.Text(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "Show S01E02-E03 - A.nfo"));
        Assert.Contains("<episode>2</episode>", nfo, StringComparison.Ordinal);
        Assert.Contains("<episodenumberend>3</episodenumberend>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachServerGetsTheTvshowNfoOfItsOwnSeriesEvenWithALocalOne()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("shows-b", "Serie B", "tvshows"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s") with { Overview = "Plot of A" });
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2));
        _fixture.Sync.Client.Add("shows-b", RemoteItemType.Series, TestItems.Series("sb") with { Overview = "Plot of B" });
        _fixture.Sync.Client.Add("shows-b", RemoteItemType.Episode, TestItems.Episode("eb", "sb", 1, 2));
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        var b = await _fixture.Sync.AddServerAsync("B", "shows-b");
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

        var ofA = StubFixture.Text(_fixture.Path_(a, "Serie", "Show (2020)", "tvshow.nfo"));
        var ofB = StubFixture.Text(_fixture.Path_(b, "Serie", "Show (2020)", "tvshow.nfo"));
        Assert.Contains("<plot>Plot of A</plot>", ofA, StringComparison.Ordinal);
        Assert.Contains("<plot>Plot of B</plot>", ofB, StringComparison.Ordinal);
        Assert.All([ofA, ofB], tvshow =>
        {
            Assert.Contains("<title>Show</title>", tvshow, StringComparison.Ordinal);
            Assert.Contains("<uniqueid type=\"imdb\">tt71</uniqueid>", tvshow, StringComparison.Ordinal);
            Assert.DoesNotContain("Local plot", tvshow, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ASyncMaterializesAndShowsWhatIsHeld()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        var a = await _fixture.Sync.AddServerAsync("A", "films");
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
        var a = await _fixture.Sync.AddServerAsync("A", "films");
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

    [Fact]
    public async Task ASyncFollowsTheServerLibrariesAfterMaterializing()
    {
        var steps = new List<string>();
        var materializer = new Mock<ILibraryMaterializer>();
        materializer.Setup(item => item.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("materialize"))
            .Returns(Task.FromResult<MaterializationReport>(null!));
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(item => item.ReconcileAsync(It.IsAny<CancellationToken>())).Callback(() => steps.Add("follow")).Returns(Task.CompletedTask);
        using var sync = SyncWith(materializer.Object, libraries.Object);

        await sync.RunAsync(CancellationToken.None);

        Assert.Equal(["materialize", "follow"], steps);
    }

    [Fact]
    public async Task AFailureInFollowingTheServerLibrariesDoesNotFailTheSync()
    {
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(item => item.ReconcileAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException(TestValues.Host));
        using var sync = SyncWith(Mock.Of<ILibraryMaterializer>(), libraries.Object);

        Assert.Equal(SyncStartResult.Started, await sync.RunAsync(CancellationToken.None));

        Assert.False(sync.GetStatus().IsRunning);
        Assert.DoesNotContain(_fixture.Sync.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
        Assert.Contains(_fixture.Sync.Logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureInFollowingTheServerLibrariesMarksTheCheckAsSkipped()
    {
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(item => item.ReconcileAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));
        using var sync = SyncWith(Mock.Of<ILibraryMaterializer>(), libraries.Object);

        await sync.RunAsync(CancellationToken.None);

        libraries.Verify(item => item.MarkFollowSkipped(), Times.Once);
    }

    [Fact]
    public async Task ALibraryCheckCancelledBeforeItRunsIsMarkedAsSkipped()
    {
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(item => item.ReconcileAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        using var sync = SyncWith(Mock.Of<ILibraryMaterializer>(), libraries.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.RunAsync(CancellationToken.None));

        libraries.Verify(item => item.MarkFollowSkipped(), Times.Once);
    }

    [Fact]
    public async Task ASyncSuspendedOnTheCredentialKeyMarksTheLibraryCheckAsSkipped()
    {
        var libraries = new Mock<IServerLibraryManager>();
        using var sync = SyncWith(Mock.Of<ILibraryMaterializer>(), libraries.Object);
        _fixture.Sync.KeySource.Key = null;

        Assert.Equal(SyncStartResult.Suspended, await sync.RunIncrementalAsync(CancellationToken.None));
        Assert.Equal(SyncStartResult.Suspended, sync.TryStart());

        libraries.Verify(item => item.MarkFollowSkipped(), Times.Exactly(2));
        libraries.Verify(item => item.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASyncStoppedBeforeTheLibraryCheckMarksItAsSkipped()
    {
        var index = new Mock<IFederationIndex>();
        index.Setup(item => item.DropServersAsync(It.IsAny<IReadOnlySet<Guid>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk"));
        var libraries = new Mock<IServerLibraryManager>();
        using var sync = new CatalogSyncService(
            _fixture.Sync.Registry,
            _fixture.Sync.Reader,
            index.Object,
            _fixture.Sync.Vault,
            _fixture.Sync.Clock,
            _fixture.Sync.Logger,
            Mock.Of<ILibraryMaterializer>(),
            libraries: libraries.Object);

        await Assert.ThrowsAsync<IOException>(() => sync.RunAsync(CancellationToken.None));

        libraries.Verify(item => item.MarkFollowSkipped(), Times.Once);
        libraries.Verify(item => item.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASyncThatReachesTheLibraryCheckDoesNotMarkItAsSkipped()
    {
        var libraries = new Mock<IServerLibraryManager>();
        using var sync = SyncWith(Mock.Of<ILibraryMaterializer>(), libraries.Object);

        await sync.RunAsync(CancellationToken.None);

        libraries.Verify(item => item.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Once);
        libraries.Verify(item => item.MarkFollowSkipped(), Times.Never);
    }

    private CatalogSyncService SyncWith(ILibraryMaterializer materializer, IServerLibraryManager libraries) => new(
        _fixture.Sync.Registry,
        _fixture.Sync.Reader,
        _fixture.Sync.Index,
        _fixture.Sync.Vault,
        _fixture.Sync.Clock,
        _fixture.Sync.Logger,
        materializer,
        libraries: libraries);

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
