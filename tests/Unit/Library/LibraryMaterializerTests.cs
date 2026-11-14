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

    private static RemoteItem Season(string id, int number, string seriesId = "s") => TestItems.Season(id, seriesId, number) with
    {
        Name = number == 1 ? "The Beginning" : "Season " + number,
        Overview = "Season plot",
        ImageTags = new Dictionary<string, string> { ["Primary"] = id + "p" }
    };

    private void AddSeasonedShow()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Season, Season("ss1", 1));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2) with { SeasonId = "ss1" });
    }

    private static Dictionary<string, string> AllArt(string prefix) => new()
    {
        ["Primary"] = prefix + "p",
        ["Logo"] = prefix + "l",
        ["Banner"] = prefix + "b",
        ["Thumb"] = prefix + "t"
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
    public async Task AFilmGetsItsLogoBannerAndThumbUnderTheNamesJellyfinReads()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt() with { ImageTags = AllArt("f") });
        var a = await _fixture.Sync.AddServerAsync("A", "films");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            ["banner.jpg", "fanart.jpg", "landscape.jpg", "logo.jpg", "poster.jpg"],
            _fixture.Files.Where(file => file.EndsWith(".jpg", StringComparison.Ordinal)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("m/Logo/0", _fixture.Sync.Client.ImageReads);
        Assert.Contains("m/Banner/0", _fixture.Sync.Client.ImageReads);
        Assert.Contains("m/Thumb/0", _fixture.Sync.Client.ImageReads);
        Assert.True(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "landscape.jpg")));
    }

    [Fact]
    public async Task ASeriesGetsItsLogoBannerAndThumbAndAnEpisodeKeepsOneImage()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s") with { ImageTags = AllArt("s") });
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2) with { ImageTags = AllArt("e") });
        var a = await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            [
                StubFixture.In(a, "Serie", "Show (2020)", "Season 01", "Show S01E02 - A-thumb.jpg"),
                StubFixture.In(a, "Serie", "Show (2020)", "banner.jpg"),
                StubFixture.In(a, "Serie", "Show (2020)", "landscape.jpg"),
                StubFixture.In(a, "Serie", "Show (2020)", "logo.jpg"),
                StubFixture.In(a, "Serie", "Show (2020)", "poster.jpg")
            ],
            _fixture.Files.Where(file => file.EndsWith(".jpg", StringComparison.Ordinal)));
        Assert.Equal(["e/Primary/0"], _fixture.Sync.Client.ImageReads.Where(read => read.StartsWith("e/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ANewLogoReachesAnIndexedFilm()
    {
        var film = FilmWithArt();
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, film);
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film" };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = film with { ImageTags = new Dictionary<string, string> { ["Primary"] = "p1", ["Logo"] = "l1" } };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(_fixture.Path_(a, "Film", "Film (2020)", "logo.jpg"), item.GetImagePath(ImageType.Logo, 0));
    }

    [Fact]
    public async Task ALogoBannerAndThumbTheRemoteRemovesLeaveTheDiskAndTheItem()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt() with { ImageTags = AllArt("f") });
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var folder = _fixture.Path_(a, "Film", "Film (2020)");
        var path = Path.Combine(folder, "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film" };
        foreach (var (type, file) in new[] { (ImageType.Logo, "logo.jpg"), (ImageType.Banner, "banner.jpg"), (ImageType.Thumb, "landscape.jpg") })
        {
            item.AddImage(new ItemImageInfo { Path = Path.Combine(folder, file), Type = type });
        }

        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = FilmWithArt() with { ImageTags = new Dictionary<string, string> { ["Primary"] = "fp" } };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            ["fanart.jpg", "poster.jpg"],
            _fixture.Files.Where(file => file.EndsWith(".jpg", StringComparison.Ordinal)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Null(item.GetImageInfo(ImageType.Logo, 0));
        Assert.Null(item.GetImageInfo(ImageType.Banner, 0));
        Assert.Null(item.GetImageInfo(ImageType.Thumb, 0));
    }

    [Fact]
    public async Task AnImageTheAdminSetElsewhereStaysOnTheItem()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt() with { ImageTags = AllArt("f") });
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film" };
        item.AddImage(new ItemImageInfo { Path = "/config/metadata/logo.png", Type = ImageType.Logo });
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = FilmWithArt() with { ImageTags = new Dictionary<string, string> { ["Primary"] = "fp" } };

        await _fixture.SyncAndMaterializeAsync();

        Assert.False(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "logo.jpg")));
        Assert.Equal("/config/metadata/logo.png", item.GetImagePath(ImageType.Logo, 0));
    }

    [Theory]
    [InlineData(RemoteFailure.Unreachable)]
    [InlineData(RemoteFailure.ServerError)]
    public async Task ARemoteThatDoesNotAnswerRemovesNoImage(RemoteFailure failure)
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt() with { ImageTags = AllArt("f") });
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)][0] = FilmWithArt();
        _fixture.Sync.Client.OnPage = (_, _) => failure;

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            ["banner.jpg", "fanart.jpg", "landscape.jpg", "logo.jpg", "poster.jpg"],
            _fixture.Files.Where(file => file.EndsWith(".jpg", StringComparison.Ordinal)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "logo.jpg")));
    }

    [Fact]
    public async Task APosterTheRemoteRemovesLeavesTheDisk()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s") with { ImageTags = AllArt("s") });
        _fixture.Sync.Client.Add("shows", RemoteItemType.Season, Season("ss1", 1));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2) with { SeasonId = "ss1", ImageTags = AllArt("e") });
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var season = _fixture.Path_(a, "Serie", "Show (2020)", "Season 01");
        var item = new Season { Id = Guid.NewGuid(), Path = season, Name = "Season 1", IndexNumber = 1 };
        item.AddImage(new ItemImageInfo { Path = Path.Combine(season, "poster.jpg"), Type = ImageType.Primary });
        _fixture.Library.Setup(library => library.FindByPath(season, true)).Returns(item);
        _fixture.Sync.Client.Items[("shows", RemoteItemType.Series)][0] = TestItems.Series("s") with { ImageTags = new Dictionary<string, string> { ["Logo"] = "sl" } };
        _fixture.Sync.Client.Items[("shows", RemoteItemType.Season)][0] = Season("ss1", 1) with { ImageTags = new Dictionary<string, string>() };
        _fixture.Sync.Client.Items[("shows", RemoteItemType.Episode)][0] = TestItems.Episode("e", "s", 1, 2) with { SeasonId = "ss1" };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(
            [StubFixture.In(a, "Serie", "Show (2020)", "logo.jpg")],
            _fixture.Files.Where(file => file.EndsWith(".jpg", StringComparison.Ordinal)));
        Assert.Null(item.GetImageInfo(ImageType.Primary, 0));
    }

    [Fact]
    public async Task ASeasonGetsItsNfoAndPosterInItsFolder()
    {
        AddSeasonedShow();
        var a = await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        var nfo = StubFixture.Text(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "season.nfo"));
        Assert.Contains("<title>The Beginning</title>", nfo, StringComparison.Ordinal);
        Assert.Contains("<plot>Season plot</plot>", nfo, StringComparison.Ordinal);
        Assert.Contains("<seasonnumber>1</seasonnumber>", nfo, StringComparison.Ordinal);
        Assert.Contains("<lockedfields>Name|Overview</lockedfields>", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("<lockdata>", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, nfo, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, nfo, StringComparison.Ordinal);
        Assert.True(File.Exists(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "poster.jpg")));
        Assert.Contains("ss1/Primary/0", _fixture.Sync.Client.ImageReads);
    }

    [Fact]
    public async Task WithoutProtectionASeasonNfoHasItsNameAndNumberOnly()
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = false;
        AddSeasonedShow();
        var a = await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        var nfo = StubFixture.Text(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "season.nfo"));
        Assert.Contains("<title>The Beginning</title>", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("<plot>", nfo, StringComparison.Ordinal);
        Assert.DoesNotContain("<lockedfields>", nfo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASeasonWithoutEpisodesGetsNoFolder()
    {
        AddSeasonedShow();
        _fixture.Sync.Client.Add("shows", RemoteItemType.Season, Season("ss2", 2));
        var a = await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        Assert.False(Directory.Exists(_fixture.Path_(a, "Serie", "Show (2020)", "Season 02")));
        Assert.DoesNotContain("ss2/Primary/0", _fixture.Sync.Client.ImageReads);
    }

    [Fact]
    public async Task AnEpisodeWithoutItsSeasonIdFindsTheSeasonOfItsSeriesByNumber()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Season, Season("ss1", 1));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2));
        var a = await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Contains("<title>The Beginning</title>", StubFixture.Text(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "season.nfo")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASeasonTheIndexDoesNotHaveGetsNoNfo()
    {
        _fixture.Sync.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e", "s", 1, 2) with { SeasonId = "missing" });
        var a = await _fixture.Sync.AddServerAsync("A", "shows");

        await _fixture.SyncAndMaterializeAsync();

        Assert.True(Directory.Exists(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01")));
        Assert.False(File.Exists(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "season.nfo")));
    }

    [Fact]
    public async Task ASecondRunLeavesTheSeasonAlone()
    {
        AddSeasonedShow();
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var nfo = _fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "season.nfo");
        var written = File.GetLastWriteTimeUtc(nfo);
        var images = _fixture.Sync.Client.ImageReads.Count;

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal(images, _fixture.Sync.Client.ImageReads.Count);
        Assert.Equal(written, File.GetLastWriteTimeUtc(nfo));
        _fixture.Library.Verify(library => library.FindByPath(It.IsAny<string>(), true), Times.Never);
    }

    [Fact]
    public async Task ALostSeasonNfoIsWrittenAgain()
    {
        AddSeasonedShow();
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var nfo = _fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "season.nfo");
        File.Delete(nfo);

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.True(File.Exists(nfo));
    }

    [Fact]
    public async Task AChangedSeasonReachesItsIndexedItem()
    {
        AddSeasonedShow();
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var folder = _fixture.Path_(a, "Serie", "Show (2020)", "Season 01");
        var item = new Season { Id = Guid.NewGuid(), Path = folder, Name = "Season 1", IndexNumber = 1 };
        _fixture.Library.Setup(library => library.FindByPath(folder, true)).Returns(item);
        _fixture.Sync.Client.Items[("shows", RemoteItemType.Season)][0] = Season("ss1", 1) with
        {
            Name = "New Name",
            Overview = "New plot",
            ImageTags = new Dictionary<string, string> { ["Primary"] = "sp2" }
        };

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal("New Name", item.Name);
        Assert.Equal("New plot", item.Overview);
        Assert.Equal([MetadataField.Name, MetadataField.Overview], item.LockedFields);
        Assert.False(item.IsLocked);
        Assert.Equal(Path.Combine(folder, "poster.jpg"), item.GetImagePath(ImageType.Primary, 0));
    }

    [Fact]
    public async Task AnIndexedSeasonGetsItsNameAndPosterOnTheFirstRunThatKnowsIt()
    {
        AddSeasonedShow();
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var series = Assert.Single(_fixture.Registry.State.Series);
        await _fixture.Registry.EnterAsync(CancellationToken.None);
        try
        {
            await _fixture.Registry.SaveAsync(_fixture.Registry.State with { Series = [series with { Seasons = [] }] }, CancellationToken.None);
        }
        finally
        {
            _fixture.Registry.Exit();
        }

        var folder = _fixture.Path_(a, "Serie", "Show (2020)", "Season 01");
        var item = new Season { Id = Guid.NewGuid(), Path = folder, Name = "Season 1", IndexNumber = 1 };
        _fixture.Library.Setup(library => library.FindByPath(folder, true)).Returns(item);

        await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal("The Beginning", item.Name);
        Assert.Equal(Path.Combine(folder, "poster.jpg"), item.GetImagePath(ImageType.Primary, 0));
    }

    [Fact]
    public async Task ASeasonThatGoesAndComesBackGetsItsFilesAgain()
    {
        AddSeasonedShow();
        _fixture.Sync.Client.Add("shows", RemoteItemType.Season, Season("ss2", 2) with { Name = "Second" });
        _fixture.Sync.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e2", "s", 2, 1) with { SeasonId = "ss2" });
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        var second = _fixture.Path_(a, "Serie", "Show (2020)", "Season 02");
        Assert.True(File.Exists(Path.Combine(second, "season.nfo")));
        var episodes = _fixture.Sync.Client.Items[("shows", RemoteItemType.Episode)];
        var gone = episodes[1];
        episodes.RemoveAt(1);

        await _fixture.SyncAndMaterializeAsync();
        Assert.False(Directory.Exists(second));
        Assert.Equal([StubFixture.In(a, "Serie", "Show (2020)", "Season 01")], Assert.Single(_fixture.Registry.State.Series).Seasons.Select(season => season.RelativePath));

        episodes.Add(gone);
        await _fixture.SyncAndMaterializeAsync();

        Assert.True(File.Exists(Path.Combine(second, "season.nfo")));
        Assert.True(File.Exists(Path.Combine(second, "poster.jpg")));
    }

    [Fact]
    public async Task AServerFolderGetsTheSeasonOfItsOwnServer()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary("shows-b", "Serie B", "tvshows"));
        AddSeasonedShow();
        _fixture.Sync.Client.Add("shows-b", RemoteItemType.Series, TestItems.Series("sb"));
        _fixture.Sync.Client.Add("shows-b", RemoteItemType.Season, Season("sb1", 1, "sb") with { Name = "Name on B" });
        _fixture.Sync.Client.Add("shows-b", RemoteItemType.Episode, TestItems.Episode("eb", "sb", 1, 2) with { SeasonId = "sb1" });
        var a = await _fixture.Sync.AddServerAsync("A", "shows");
        var b = await _fixture.Sync.AddServerAsync("B", "shows-b");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Contains("<title>The Beginning</title>", StubFixture.Text(_fixture.Path_(a, "Serie", "Show (2020)", "Season 01", "season.nfo")), StringComparison.Ordinal);
        Assert.Contains("<title>Name on B</title>", StubFixture.Text(_fixture.Path_(b, "Serie", "Show (2020)", "Season 01", "season.nfo")), StringComparison.Ordinal);
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

        // The fake remote answers at once, so the removal is over when the call returns.
        _fixture.AfterResolve = () => _ = _fixture.Sync.Registry.RemoveAsync(server.Id);
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

    // The sync keeps a run as the one to compare with only when it left nothing for the next run (Q386).
    [Fact]
    public async Task TheReportSaysWhatTheRunLeftForTheNextOne()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        _fixture.Sync.Client.ImageFailure = RemoteFailure.ServerError;
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);

        var first = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);
        _fixture.Sync.Client.ImageFailure = RemoteFailure.None;
        var second = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);
        var third = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.Equal((2, true, 0), (first.ImagesPending, first.ScanQueued, first.AwaitingRefresh));
        Assert.Equal((0, false), (second.ImagesPending, second.ScanQueued));
        Assert.Equal(_fixture.Registry.Generation, third.RegistryGeneration);
        Assert.True(second.RegistryGeneration > first.RegistryGeneration);
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

    // Q276: the stub leaves its version group, with no metadata save, before Jellyfin deletes its row.
    [Fact]
    public async Task ARemovedStubLeavesItsVersionGroupBeforeItsRowIsDeleted()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"), TestItems.Movie("b", "Other", 2021, "2"));
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        var steps = new List<string>();
        _fixture.Linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<StubEntry> stubs, CancellationToken _) => steps.Add("detach " + string.Join(",", stubs.Select(stub => Path.GetFileName(stub.RelativePath)))))
            .ReturnsAsync([]);
        _fixture.Library.Setup(library => library.DeleteItem(item, It.IsAny<DeleteOptions>())).Callback(() => steps.Add("delete"));
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].RemoveAt(0);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["detach Film (2020) - A.strm", "delete"], steps);
    }

    // Q351: during a server rename the old item is still in its version group, and a film's folder takes its file too, so
    // it leaves the group with the stub.
    [Fact]
    public async Task ARemovedStubWithAPendingRenameDetachesItsOldItemToo()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"), TestItems.Movie("b", "Other", 2021, "2"));
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var oldPath = await PendingRenameAsync(a, "Film (2020)");
        var detached = new List<string>();
        _fixture.Linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyCollection<StubEntry> stubs, CancellationToken _) => detached.AddRange(stubs.Select(stub => Path.GetFileName(stub.RelativePath))))
            .ReturnsAsync([]);
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].RemoveAt(0);

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["Film (2020) - A.strm", Path.GetFileName(oldPath)], detached);
        Assert.False(Directory.Exists(_fixture.Path_(a, "Film", "Film (2020)")));
    }

    // Q351 and Q326: an old item that cannot leave its group keeps the stub, counted once, for the next round.
    [Fact]
    public async Task ARemovedStubWhoseOldItemCannotLeaveItsGroupStaysUntilTheNextRound()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"), TestItems.Movie("b", "Other", 2021, "2"));
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var oldPath = await PendingRenameAsync(a, "Film (2020)");
        _fixture.Linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<StubEntry> stubs, CancellationToken _) => stubs.Where(stub => stub.RelativePath == oldPath).ToList());
        _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)].RemoveAt(0);

        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.True(File.Exists(_fixture.Path_(oldPath)));
        Assert.True(File.Exists(_fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm")));
        Assert.Equal(2, _fixture.Registry.State.Stubs.Count);
        Assert.Equal(1, report.Failed);
        Assert.Equal(new StubCounts(0, 0, 0, 1), report.ByServer[a.Id]);

        _fixture.Linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        await _fixture.SyncAndMaterializeAsync();

        Assert.False(File.Exists(_fixture.Path_(oldPath)));
        Assert.Single(_fixture.Registry.State.Stubs);
    }

    // Q326: a stub whose detach failed stays, with its file and registry entry, and the next round removes it.
    [Fact]
    public async Task AStubWhoseDetachFailedStaysUntilTheNextRound()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"), TestItems.Movie("b", "Other", 2021, "2"), TestItems.Movie("c", "Third", 2022, "3"));
        var a = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var film = _fixture.Path_(a, "Film", "Film (2020)", "Film (2020) - A.strm");
        var third = _fixture.Path_(a, "Film", "Third (2022)", "Third (2022) - A.strm");
        _fixture.Linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<StubEntry> stubs, CancellationToken _) => stubs.Where(stub => stub.RelativePath.Contains("Film (2020)", StringComparison.Ordinal)).ToList());
        var items = _fixture.Sync.Client.Items[("films", RemoteItemType.Movie)];
        items.RemoveAll(item => item.Id is "a" or "c");

        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        var report = await _fixture.Materializer.MaterializeAsync(CancellationToken.None);

        Assert.True(File.Exists(film));
        Assert.False(File.Exists(third));
        Assert.Contains(_fixture.Registry.State.Stubs, stub => stub.RelativePath.Contains("Film (2020)", StringComparison.Ordinal));
        Assert.Equal(1, report.Removed);
        Assert.Equal(1, report.Failed);
        Assert.Equal(new StubCounts(0, 0, 1, 1), report.ByServer[a.Id]);

        _fixture.Linker.Setup(linker => linker.DetachForRemovalAsync(It.IsAny<IReadOnlyCollection<StubEntry>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        await _fixture.SyncAndMaterializeAsync();

        Assert.False(File.Exists(film));
        Assert.Single(_fixture.Registry.State.Stubs);
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

        await sync.RemoveServerAsync(removed.Id, () => _fixture.Sync.Registry.RemoveAsync(removed.Id), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_fixture.Path_(removed, "Film", "Other (2021)")));
        Assert.False(Directory.Exists(_fixture.Path_(removed, "Film", "Film (2020)")));
        Assert.False(Directory.Exists(_fixture.Path_(removed)));
        Assert.True(File.Exists(_fixture.Path_(kept, "Film", ".jellymesh")));
        Assert.True(File.Exists(_fixture.Path_(kept, "Film", "Film (2020)", "Film (2020) - B.strm")));
        Assert.Equal(kept.Id, Assert.Single(_fixture.Registry.State.Stubs).ServerId);
        Assert.Null(_fixture.Sync.Index.Snapshot.Find(removed.Id));
        _fixture.Library.Verify(library => library.DeleteItem(item, It.Is<DeleteOptions>(options => !options.DeleteFileLocation)), Times.Once);
    }

    // Row 209: the last server removed also loses its folder, with its series.
    [Fact]
    public async Task RemovingTheLastServerDeletesItsFolderAndItsSeries()
    {
        AddSeasonedShow();
        var removed = await _fixture.Sync.AddServerAsync("A", "shows");
        await _fixture.SyncAndMaterializeAsync();
        Assert.Single(_fixture.Registry.State.Series);
        using var sync = SyncWithMaterializer();

        await sync.RemoveServerAsync(removed.Id, () => _fixture.Sync.Registry.RemoveAsync(removed.Id), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_fixture.Path_(removed)));
        Assert.Empty(_fixture.Registry.State.Stubs);
        Assert.Empty(_fixture.Registry.State.Series);
        Assert.Equal([removed.Id], _fixture.Registry.State.RemovedServers);
    }

    [Fact]
    public async Task ARemovedServerWhoseStubCannotGoKeepsItsFolderUntilItDoes()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var removed = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        var path = _fixture.Path_(removed, "Film", "Film (2020)", "Film (2020) - A.strm");
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Throws(new IOException("busy"));
        using var sync = SyncWithMaterializer();
        await sync.RemoveServerAsync(removed.Id, () => _fixture.Sync.Registry.RemoveAsync(removed.Id), CancellationToken.None);

        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Single(_fixture.Registry.State.Stubs);

        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns((BaseItem?)null);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.False(Directory.Exists(_fixture.Path_(removed)));
        Assert.Empty(_fixture.Registry.State.Stubs);
    }

    [Fact]
    public async Task AServerMissingFromAResetConfigurationKeepsItsFolder()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var server = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        using var sync = SyncWithMaterializer();

        await _fixture.Sync.Registry.RemoveAsync(server.Id);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);

        Assert.True(File.Exists(_fixture.Path_(server, "Film", "Film (2020)", "Film (2020) - A.strm")));
        Assert.True(File.Exists(_fixture.Path_(server, "Film", ".jellymesh")));
    }

    // Decision 408: a removed remote added again is a new server, in a new folder, and the label it had is free again.
    [Fact]
    public async Task AServerAddedBackIsANewServerAndTakesTheFreedLabel()
    {
        _fixture.Sync.Client.ServerId = RemoteServerId;
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var first = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        using var sync = SyncWithMaterializer();
        await sync.RemoveServerAsync(first.Id, () => _fixture.Sync.Registry.RemoveAsync(first.Id), CancellationToken.None);
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        Assert.False(Directory.Exists(_fixture.Path_(first)));

        var again = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();

        Assert.NotEqual(first.Id, again.Id);
        Assert.True(File.Exists(_fixture.Path_(again, "Film", "Film (2020)", "Film (2020) - A.strm")));
        Assert.False(Directory.Exists(_fixture.Path_(first)));
        Assert.Equal(again.Id, Assert.Single(_fixture.Registry.State.Stubs).ServerId);
        Assert.False(_fixture.Registry.State.Labels.ContainsKey(first.Id));
    }

    [Fact]
    public async Task AServerAddedBackBeforeTheCleanupLeavesTheOldFolderToGo()
    {
        _fixture.Sync.Client.ServerId = RemoteServerId;
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var first = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        using var sync = SyncWithMaterializer();
        await sync.RemoveServerAsync(first.Id, () => _fixture.Sync.Registry.RemoveAsync(first.Id), CancellationToken.None);

        var again = await _fixture.Sync.AddServerAsync("A", "films");
        await sync.CleanUpAfterRemovalAsync(CancellationToken.None);
        await _fixture.SyncAndMaterializeAsync();

        Assert.NotEqual(first.Id, again.Id);
        Assert.False(Directory.Exists(_fixture.Path_(first)));
        Assert.Null(_fixture.Sync.Index.Snapshot.Find(first.Id));
        Assert.True(File.Exists(_fixture.Path_(again, "Film", "Film (2020)", "Film (2020) - A.strm")));
        Assert.Equal(again.Id, Assert.Single(_fixture.Registry.State.Stubs).ServerId);
    }

    [Fact]
    public async Task TheLabelOfAServerMissingFromTheSettingsStaysTaken()
    {
        _fixture.Sync.Client.ServerId = RemoteServerId;
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var first = await _fixture.Sync.AddServerAsync("A", "films");
        using var sync = SyncWithMaterializer();
        await sync.RunAsync(CancellationToken.None);
        await _fixture.Sync.Registry.RemoveAsync(first.Id);
        _fixture.Sync.Client.ServerId = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";

        var other = await _fixture.Sync.AddServerAsync("A", "films");
        await sync.RunAsync(CancellationToken.None);

        var stub = Path.GetFileName(Assert.Single(Directory.GetFiles(_fixture.Path_(other, "Film", "Film (2020)"), "*.strm")));
        Assert.StartsWith("Film (2020) - A ", stub, StringComparison.Ordinal);
        Assert.Equal("A", _fixture.Registry.State.Labels[first.Id]);
        Assert.True(File.Exists(_fixture.Path_(first, "Film", "Film (2020)", "Film (2020) - A.strm")));
    }

    [Fact]
    public async Task RemovingAServerDeletesItsContentWhileTheKeyIsBroken()
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt("a", "1"));
        var removed = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();
        using var sync = SyncWithMaterializer();

        _fixture.Sync.KeySource.Key = null;
        await sync.RemoveServerAsync(removed.Id, () => _fixture.Sync.Registry.RemoveAsync(removed.Id), CancellationToken.None);
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

        await _fixture.Sync.Registry.RemoveAsync(server.Id);
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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ANewLocalFilmIsRefreshedAndItsCaseWaits(bool awaitsRefresh, bool logged)
    {
        _fixture.Sync.Client.Add("films", RemoteItemType.Movie, FilmWithArt());
        await _fixture.Sync.AddServerAsync("A", "films");
        var local = Guid.NewGuid();
        _fixture.LocalCatalog.Items.Add(new CatalogItem
        {
            ServerId = "local",
            ItemId = local.ToString("N"),
            Kind = ContentKind.Movie,
            Title = "Film",
            Year = 2020,
            Metadata = new ComparableMetadata { Overview = "Altro" },
            AwaitsRefresh = awaitsRefresh
        });

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(awaitsRefresh ? [local] : [], _fixture.Refresher.Requested);
        Assert.Equal(awaitsRefresh ? 1 : 0, _fixture.Materializer.LastReport!.AwaitingRefresh);
        Assert.Equal(logged, _fixture.Logger.Lines.Exists(line => line.Contains("may be duplicates", StringComparison.Ordinal)));
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
    public async Task ASyncWaitsForTheServerLibrariesToBeReadyBeforeItStarts()
    {
        var steps = new List<string>();
        var materializer = new Mock<ILibraryMaterializer>();
        materializer.Setup(item => item.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("materialize"))
            .Returns(Task.FromResult<MaterializationReport>(null!));
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(item => item.WaitReadyAsync(It.IsAny<CancellationToken>())).Callback(() => steps.Add("ready")).Returns(Task.CompletedTask);
        libraries.Setup(item => item.ReconcileAsync(It.IsAny<CancellationToken>())).Callback(() => steps.Add("follow")).Returns(Task.CompletedTask);
        using var sync = SyncWith(materializer.Object, libraries.Object);

        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["ready", "materialize", "follow"], steps);
    }

    [Fact]
    public async Task ASyncCancelledWhileWaitingForTheLibrariesMarksTheCheckAsSkipped()
    {
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(item => item.WaitReadyAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        using var sync = SyncWith(Mock.Of<ILibraryMaterializer>(), libraries.Object);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.RunAsync(CancellationToken.None));

        Assert.False(sync.GetStatus().IsRunning);
        libraries.Verify(item => item.ReconcileAsync(It.IsAny<CancellationToken>()), Times.Never);
        libraries.Verify(item => item.MarkFollowSkipped(), Times.Once);
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

    // Leaves the film stub of a server with a pending rename from an old label, its old .strm still on disk.
    private async Task<string> PendingRenameAsync(RemoteServerInfo server, string folder)
    {
        var stub = _fixture.Registry.State.Stubs.Single(stub => stub.RelativePath.Contains(folder, StringComparison.Ordinal));
        var oldPath = StubFixture.In(server, "Film", folder, folder + " - Vecchio.strm");
        await File.WriteAllTextAsync(_fixture.Path_(oldPath), "old");
        await _fixture.Registry.EnterAsync(CancellationToken.None);
        try
        {
            await _fixture.Registry.SaveAsync(_fixture.Registry.State with { Renames = [new StubRename(stub.Id, oldPath)] }, CancellationToken.None);
        }
        finally
        {
            _fixture.Registry.Exit();
        }

        return oldPath;
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
