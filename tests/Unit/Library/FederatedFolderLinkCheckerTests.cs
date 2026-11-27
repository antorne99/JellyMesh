using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The page shows, for each server, how its folders are linked, and warns about libraries that break the links (Q22,
// Q45, Q46, Q53, D13). The 0.5 server folders count while titles are in the older layout, then the type folders.
public sealed class FederatedFolderLinkCheckerTests
{
    private const string Folder = "/federata";
    private static readonly Guid _a = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid _b = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly string _aFilm = $"/federata/{_a:D}/Film";
    private static readonly string _aSerie = $"/federata/{_a:D}/Serie";
    private static readonly string _bFilm = $"/federata/{_b:D}/Film";
    private static readonly string _bSerie = $"/federata/{_b:D}/Serie";
    private const string FilmsA = "a1000000000000000000000000000001";
    private const string ShowsA = "a2000000000000000000000000000001";
    private const string AnimeA = "a3000000000000000000000000000001";
    private const string FilmsB = "b1000000000000000000000000000001";
    private const string ShowsB = "b2000000000000000000000000000001";
    private static readonly string _aFilms = $"/federata/{_a:D}/{FilmsA}/Film";
    private static readonly string _aShows = $"/federata/{_a:D}/{ShowsA}/Serie";
    private static readonly string _aAnime = $"/federata/{_a:D}/{AnimeA}/Serie";
    private static readonly string _bFilms = $"/federata/{_b:D}/{FilmsB}/Film";
    private static readonly string _bShows = $"/federata/{_b:D}/{ShowsB}/Serie";
    private static readonly Guid _c = Guid.Parse("cccccccc-0000-4000-8000-000000000003");
    private const string ShowsC = "c2000000000000000000000000000001";
    private static readonly string _cShows = $"/federata/{_c:D}/{ShowsC}/Serie";

    private static VirtualFolderInfo Library(string name, CollectionTypeOptions? type, bool grouping, params string[] locations) => new()
    {
        Name = name,
        CollectionType = type,
        Locations = locations,
        LibraryOptions = new LibraryOptions { EnableAutomaticSeriesGrouping = grouping }
    };

    private static VirtualFolderInfo Movies(params string[] locations) => Library("Film", CollectionTypeOptions.movies, true, locations);

    private static VirtualFolderInfo Shows(string name, params string[] locations) => Library(name, CollectionTypeOptions.tvshows, true, locations);

    private static VirtualFolderInfo WithId(VirtualFolderInfo library, string id)
    {
        library.ItemId = id;
        return library;
    }

    // A server with the given remote libraries imported, each as (id, name, collection type).
    private static RemoteServerConfig Server(Guid id, params (string Id, string Name, string Type)[] libraries)
    {
        var server = new RemoteServerConfig { Id = id, Name = id.ToString("N")[..4] };
        foreach (var (libraryId, name, type) in libraries)
        {
            server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = libraryId, Name = name, CollectionType = type });
            server.ImportedLibraryIds.Add(libraryId);
        }

        return server;
    }

    private static RemoteServerConfig ServerA(params (string Id, string Name, string Type)[] libraries) => Server(_a, libraries);

    private static RemoteServerConfig ServerB() => Server(_b, (FilmsB, "Film", "movies"), (ShowsB, "Serie", "tvshows"));

    private static FederatedFolderLinkChecker Checker(InMemoryConfigurationStore store, bool? olderLayout, params VirtualFolderInfo[] libraries)
    {
        // Strict: a call that is not this read would fail, so the test also proves the checker changes no library.
        var manager = new Mock<ILibraryManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>(libraries));
        var stubs = new Mock<IStubRegistry>();
        stubs.Setup(registry => registry.UsesOlderLayout()).Returns(olderLayout);
        return new FederatedFolderLinkChecker(manager.Object, store, stubs.Object, new CapturingLogger<FederatedFolderLinkChecker>());
    }

    // The type folder layout: the servers with their imported libraries, and the single libraries by item id.
    private static FederatedFolderLinkReport CheckTypeFolders(RemoteServerConfig[] servers, string[] shared, params VirtualFolderInfo[] libraries)
    {
        var store = new InMemoryConfigurationStore();
        foreach (var server in servers)
        {
            store.Configuration.Servers.Add(server);
        }

        foreach (var id in shared)
        {
            store.Configuration.SharedLibraries.Libraries.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Tutti i server - Serie", ItemId = id });
        }

        return Checker(store, false, libraries).Check(Folder, servers.Select(server => server.Id).ToList());
    }

    private static FederatedFolderLinkReport CheckTypeFolders(RemoteServerConfig server, params VirtualFolderInfo[] libraries) => CheckTypeFolders([server], [], libraries);

    // The 0.5 layout, with a film and a series library imported from each server.
    private static FederatedFolderLinkReport Check(string folder, Guid[] servers, params VirtualFolderInfo[] libraries)
    {
        var store = new InMemoryConfigurationStore();
        foreach (var id in servers)
        {
            store.Configuration.Servers.Add(Server(id, (id.ToString("N"), "Film", "movies"), ($"{id:N}"[..31] + "f", "Serie", "tvshows")));
        }

        return Checker(store, true, libraries).Check(folder, servers);
    }

    private static FederatedFolderLinkReport Check(params VirtualFolderInfo[] libraries) => Check(Folder, [_a], libraries);

    private static ServerFolderLink LinkOfA(FederatedFolderLinkReport report) => report.Servers.Single(server => server.ServerId == _a);

    [Fact]
    public void EachServerHasItsTwoPaths()
    {
        var report = Check(Folder, [_a, _b]);

        Assert.Equal([_a, _b], report.Servers.Select(server => server.ServerId));
        Assert.Equal([_aFilm, _aSerie], LinkOfA(report).Folders.Select(folder => folder.Path));
        Assert.Equal([_bFilm, _bSerie], report.Servers[1].Folders.Select(folder => folder.Path));
    }

    [Fact]
    public void BothPathsInLibrariesOfTheRightTypeAreLinked()
    {
        var report = Check(Movies("/film", _aFilm), Shows("Serie", _aSerie, "/serie"));

        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(report).Movies.Link);
        Assert.Equal(["Film"], LinkOfA(report).Movies.Libraries);
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(report).Series.Link);
        Assert.Empty(LinkOfA(report).SeriesGroupingOff);
    }

    [Fact]
    public void ALocationThatIsNoPathIsSkipped()
    {
        var report = Check(Movies("/film\0", _aFilm), Shows("Serie", "\0", _aSerie));

        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
        Assert.Equal(["Film"], LinkOfA(report).Movies.Libraries);
        Assert.Empty(report.Covering);
    }

    [Fact]
    public void EveryServerHasToBeLinked()
    {
        var report = Check(Folder, [_a, _b], Movies(_aFilm, _bFilm), Shows("Serie", _aSerie));

        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
        Assert.Equal(FederatedPathLink.NotLinked, report.Servers[1].Series.Link);
    }

    [Fact]
    public void TheFlatPathsLinkNoServer()
    {
        var report = Check(Movies("/federata/Film"), Shows("Serie", "/federata/Serie"));

        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Movies.Link);
        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Series.Link);
    }

    [Fact]
    public void WithNoServerThereIsNothingLinked()
    {
        var report = Check(Folder, [], Movies("/film"));

        Assert.Empty(report.Servers);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void NoLibraryAtAllIsNotLinked()
    {
        var report = Check();

        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Movies.Link);
        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Series.Link);
    }

    [Fact]
    public void OnePathAloneIsNotEnough()
    {
        var report = Check(Movies(_aFilm));

        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(report).Movies.Link);
        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Series.Link);
    }

    [Theory]
    [InlineData("/federata", "{0}/")]
    [InlineData("/federata/", "{0}")]
    [InlineData("/federata/", "{0}//")]
    [InlineData("/federata//", "/federata//aaaaaaaa-0000-4000-8000-000000000001/Film/")]
    [InlineData("/federata", "/federata/./aaaaaaaa-0000-4000-8000-000000000001/Film")]
    public void TrailingAndDoubledSeparatorsMakeNoDifference(string folder, string location)
    {
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(Check(folder, [_a], Movies(string.Format(System.Globalization.CultureInfo.InvariantCulture, location, _aFilm)))).Movies.Link);
    }

    [Theory]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001/film")]
    [InlineData("/FEDERATA/aaaaaaaa-0000-4000-8000-000000000001/Film")]
    [InlineData("/federata/AAAAAAAA-0000-4000-8000-000000000001/Film")]
    public void OtherCapitalsAreAnotherFolderHereAndSaySo(string location)
    {
        var report = Check(Movies(location));

        Assert.Equal(FederatedPathLink.DifferentCase, LinkOfA(report).Movies.Link);
        Assert.Equal(["Film"], LinkOfA(report).Movies.Libraries);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void TheExactPathWinsOverOneWithOtherCapitals()
    {
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(Check(Movies(_aFilm.ToLowerInvariant(), _aFilm))).Movies.Link);
    }

    [Fact]
    public void TheFilmPathInASeriesLibraryIsTheWrongType()
    {
        var report = Check(Library("Telefilm", CollectionTypeOptions.tvshows, true, _aFilm));

        Assert.Equal(FederatedPathLink.WrongLibraryType, LinkOfA(report).Movies.Link);
        Assert.Equal(["Telefilm"], LinkOfA(report).Movies.Libraries);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void TheSeriesPathInAFilmLibraryIsTheWrongType()
    {
        Assert.Equal(FederatedPathLink.WrongLibraryType, LinkOfA(Check(Movies(_aSerie))).Series.Link);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.homevideos)]
    [InlineData(CollectionTypeOptions.music)]
    public void AnotherLibraryTypeIsTheWrongType(CollectionTypeOptions type)
    {
        Assert.Equal(FederatedPathLink.WrongLibraryType, LinkOfA(Check(Library("Altro", type, true, _aFilm))).Movies.Link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CollectionTypeOptions.mixed)]
    public void BothPathsInMixedLibrariesAreLinked(CollectionTypeOptions? type)
    {
        var report = Check(Library("PuppyNas", type, true, _aFilm, _aSerie));

        Assert.Equal(FederatedPathLink.Linked, LinkOfA(report).Movies.Link);
        Assert.Equal(["PuppyNas"], LinkOfA(report).Movies.Libraries);
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(report).Series.Link);
        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
    }

    [Fact]
    public void ALibraryOfTheWrongTypeIsReportedEvenBesideARightOne()
    {
        var report = Check(Movies(_aFilm), Library("Telefilm", CollectionTypeOptions.tvshows, true, _aFilm));

        Assert.Equal(FederatedPathLink.WrongLibraryType, LinkOfA(report).Movies.Link);
        Assert.Equal(["Telefilm"], LinkOfA(report).Movies.Libraries);
    }

    [Theory]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001/Film2")]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001/Film/sub")]
    [InlineData("/federata/Film")]
    public void ANeighbourOfThePathDoesNotCount(string location)
    {
        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(Check(Movies(location))).Movies.Link);
    }

    [Fact]
    public void ABlankLocationIsIgnored()
    {
        var report = Check(Movies(string.Empty, " "));

        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Movies.Link);
        Assert.Empty(report.Covering);
    }

    [Fact]
    public void GroupingOffOnTheLibraryOfTheSeriesPathIsReportedForThatServer()
    {
        var report = Check(Folder, [_a, _b], Movies(_aFilm, _bFilm), Library("Serie", CollectionTypeOptions.tvshows, false, _aSerie + "/"), Shows("Altre", _bSerie));

        Assert.Equal(["Serie"], LinkOfA(report).SeriesGroupingOff);
        Assert.Empty(report.Servers[1].SeriesGroupingOff);
        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
    }

    [Fact]
    public void GroupingOffElsewhereIsNotReported()
    {
        var report = Check(Shows("Serie", _aSerie), Library("Altre", CollectionTypeOptions.tvshows, false, "/altre"));

        Assert.Empty(LinkOfA(report).SeriesGroupingOff);
    }

    [Fact]
    public void GroupingIsNotWhatIsWrongWithALibraryOfTheWrongType()
    {
        var report = Check(Library("Film", CollectionTypeOptions.movies, false, _aSerie));

        Assert.Empty(LinkOfA(report).SeriesGroupingOff);
    }

    [Theory]
    [InlineData("/federata")]
    [InlineData("/federata/")]
    [InlineData("/")]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001")]
    public void ALibraryOverTheServerFoldersCoversThem(string location)
    {
        var report = Check(Movies(_aFilm), Library("Tutto", CollectionTypeOptions.mixed, true, "/media", location));

        Assert.Equal(["Tutto"], report.Covering);
    }

    [Fact]
    public void ALibraryOnTheFederatedFolderCoversItEvenWithNoServer()
    {
        Assert.Equal(["Tutto"], Check(Folder, [], Library("Tutto", null, true, "/federata")).Covering);
    }

    [Theory]
    [InlineData("/federata2")]
    [InlineData("/fed")]
    [InlineData("/federata/Film")]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001/Film")]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001/Film/Dune (2021)")]
    [InlineData("/media")]
    public void ALibraryBesideOrBelowTheServerFoldersDoesNotCoverThem(string location)
    {
        Assert.Empty(Check(Movies(location)).Covering);
    }

    [Fact]
    public void SeriesFoldersInTheSameLibrariesMerge()
    {
        var report = Check(Folder, [_a, _b], Shows("Serie", "/media/serie", _aSerie, _bSerie), Shows("Tutte", _aSerie, _bSerie));

        Assert.False(report.SeriesSplit);
        Assert.Equal(["Serie", "Tutte"], LinkOfA(report).SeriesLibraries);
    }

    [Fact]
    public void AServerSeriesFolderInOneMoreLibrarySplitsTheSeries()
    {
        var report = Check(Folder, [_a, _b], Shows("Serie", _aSerie, _bSerie), Shows("Casa - Serie", _aSerie));

        Assert.True(report.SeriesSplit);
        Assert.Equal(["Serie", "Casa - Serie"], LinkOfA(report).SeriesLibraries);
        Assert.Equal(["Serie"], report.Servers[1].SeriesLibraries);
    }

    [Fact]
    public void TheLibrariesOfEachServerSplitTheSeriesOfTheSharedOne()
    {
        var report = Check(Folder, [_a, _b], Shows("Serie", _aSerie, _bSerie), Shows("Casa - Serie", _aSerie), Shows("Amico - Serie", _bSerie));

        Assert.True(report.SeriesSplit);
    }

    [Fact]
    public void AServerWithItsSeriesInNoLibraryIsOnlyNotLinked()
    {
        var report = Check(Folder, [_a, _b], Shows("Serie", _aSerie));

        Assert.False(report.SeriesSplit);
        Assert.Empty(report.Servers[1].SeriesLibraries);
    }

    [Fact]
    public void OneServerAloneHasNothingToMerge()
    {
        Assert.False(Check(Shows("Serie", "/media/serie", _aSerie), Shows("Casa - Serie", _aSerie)).SeriesSplit);
    }

    [Fact]
    public void FilmFoldersInOtherLibrariesSplitNothing()
    {
        var report = Check(Folder, [_a, _b], Movies(_aFilm, _bFilm), Library("Casa - Film", CollectionTypeOptions.movies, true, _aFilm), Shows("Serie", _aSerie, _bSerie));

        Assert.False(report.SeriesSplit);
    }

    [Fact]
    public void TheLibrariesWhoseOnlyPathIsAServerFolderAreNamed()
    {
        var report = Check(Library("Casa - Film", CollectionTypeOptions.movies, true, _aFilm), Shows("Casa - Serie", _aSerie + "/"), Shows("Serie", _aSerie, "/media"));

        Assert.Equal(["Casa - Film", "Casa - Serie"], LinkOfA(report).OnlyOnItsFolders);
    }

    [Fact]
    public void EachImportedLibraryOfAServerHasItsTypeFolder()
    {
        var report = CheckTypeFolders(ServerA((FilmsA, "Film", "movies"), (ShowsA, "Serie", "tvshows"), (AnimeA, "Anime", "tvshows")));

        Assert.Equal([_aFilms, _aShows, _aAnime], LinkOfA(report).Folders.Select(folder => folder.Path));
        Assert.Equal(["movies", "tvshows", "tvshows"], LinkOfA(report).Folders.Select(folder => folder.LibraryType));
        Assert.Equal(["Film", "Serie", "Anime"], LinkOfA(report).Folders.Select(folder => folder.RemoteLibrary));
    }

    [Fact]
    public void OnTheTypeFolderLayoutTheServerFoldersOf05LinkNothing()
    {
        var report = CheckTypeFolders(ServerA((FilmsA, "Film", "movies")), Movies(_aFilm));

        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Movies.Link);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void EveryTypeFolderOfAServerInALibraryOfItsTypeIsLinked()
    {
        var report = CheckTypeFolders(
            ServerA((FilmsA, "Film", "movies"), (ShowsA, "Serie", "tvshows"), (AnimeA, "Anime", "tvshows")),
            Movies(_aFilms),
            Shows("Serie", _aShows),
            Shows("Anime", _aAnime));

        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(report).Series.Link);
        Assert.Equal(["Serie", "Anime"], LinkOfA(report).Series.Libraries);
        Assert.All(LinkOfA(report).Folders, folder => Assert.Equal(FederatedPathLink.Linked, folder.Link.Link));
    }

    [Fact]
    public void OneTypeFolderLeftOutLeavesItsKindNotLinked()
    {
        var report = CheckTypeFolders(
            ServerA((FilmsA, "Film", "movies"), (ShowsA, "Serie", "tvshows"), (AnimeA, "Anime", "tvshows")),
            Movies(_aFilms),
            Shows("Serie", _aShows));

        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Series.Link);
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(report).Folders.Single(folder => folder.Path == _aShows).Link.Link);
        Assert.Equal(FederatedPathLink.NotLinked, LinkOfA(report).Folders.Single(folder => folder.Path == _aAnime).Link.Link);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void ATypeFolderInALibraryOfAnotherTypeMakesItsKindTheWrongType()
    {
        var report = CheckTypeFolders(
            ServerA((ShowsA, "Serie", "tvshows"), (AnimeA, "Anime", "tvshows")),
            Shows("Serie", _aShows),
            Library("Cartoni", CollectionTypeOptions.movies, true, _aAnime));

        Assert.Equal(FederatedPathLink.WrongLibraryType, LinkOfA(report).Series.Link);
        Assert.Equal(["Cartoni"], LinkOfA(report).Series.Libraries);
    }

    [Fact]
    public void AServerWithNoSeriesLibraryNeedsOnlyItsFilmsLinked()
    {
        var report = CheckTypeFolders([ServerA((FilmsA, "Film", "movies")), ServerB()], [], Movies(_aFilms, _bFilms), Shows("Serie", _bShows));

        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
        Assert.Equal(FederatedPathLink.Linked, LinkOfA(report).Series.Link);
        Assert.Empty(LinkOfA(report).Series.Libraries);
        Assert.False(report.SeriesSplit);
        Assert.Empty(LinkOfA(report).SeriesSplitWith);
    }

    [Fact]
    public void ALibraryOnTheFolderOfARemoteLibraryCoversItsTypeFolders()
    {
        var report = CheckTypeFolders(ServerA((FilmsA, "Film", "movies")), Library("Tutto", CollectionTypeOptions.mixed, true, $"/federata/{_a:D}/{FilmsA}"));

        Assert.Equal(["Tutto"], report.Covering);
    }

    [Fact]
    public void TheSeriesOfAServerInItsOwnLibraryAsWellSplitFromTheOthers()
    {
        var report = CheckTypeFolders(
            [ServerA((ShowsA, "Serie", "tvshows")), ServerB()],
            [],
            Shows("Serie", _aShows, _bShows),
            Shows("Casa - Serie", _aShows));

        Assert.True(report.SeriesSplit);
        Assert.Equal(["Serie", "Casa - Serie"], LinkOfA(report).SeriesLibraries);
        Assert.Equal([_b], LinkOfA(report).SeriesSplitWith);
        Assert.Equal([_a], report.Servers[1].SeriesSplitWith);
    }

    [Fact]
    public void EachServerNamesOnlyTheServersItsSeriesSplitFrom()
    {
        var report = CheckTypeFolders(
            [ServerA((ShowsA, "Serie", "tvshows")), ServerB(), Server(_c, (ShowsC, "Serie", "tvshows"))],
            [],
            Shows("Serie", _aShows, _bShows, _cShows),
            Shows("Casa - Serie", _aShows));

        Assert.Equal([_b, _c], LinkOfA(report).SeriesSplitWith);
        Assert.Equal([_a], report.Servers[1].SeriesSplitWith);
        Assert.Equal([_a], report.Servers[2].SeriesSplitWith);
    }

    [Fact]
    public void AServerWhoseSeriesShareNoLibrarySplitsFromNoOne()
    {
        var report = CheckTypeFolders(
            [ServerA((ShowsA, "Serie", "tvshows")), ServerB(), Server(_c, (ShowsC, "Serie", "tvshows"))],
            [],
            Shows("Serie", _aShows, _bShows),
            Shows("Casa - Serie", _aShows),
            Shows("Altro - Serie", _cShows));

        Assert.Equal([_b], LinkOfA(report).SeriesSplitWith);
        Assert.Empty(report.Servers[2].SeriesSplitWith);
    }

    [Fact]
    public void SeriesFoldersOfTwoServersInTheSameLibrariesMerge()
    {
        var report = CheckTypeFolders(
            [ServerA((ShowsA, "Serie", "tvshows")), ServerB()],
            [],
            Shows("Serie", _aShows, _bShows),
            Shows("Tutte", _aShows, _bShows));

        Assert.False(report.SeriesSplit);
    }

    [Fact]
    public void TheSingleLibraryCountsInNoSetOfLibraries()
    {
        var report = CheckTypeFolders(
            [ServerA((ShowsA, "Serie", "tvshows")), ServerB()],
            ["5000000000000000000000000000000a"],
            Shows("Serie", _aShows, _bShows),
            WithId(Shows("Tutte le serie", _aShows), "5000000000000000000000000000000a"));

        Assert.False(report.SeriesSplit);
        Assert.Equal(["Serie"], LinkOfA(report).SeriesLibraries);
    }

    [Fact]
    public void ARemoteLibraryKeptInALibraryOfItsOwnSplitsNothing()
    {
        var report = CheckTypeFolders(
            [ServerA((ShowsA, "Serie", "tvshows"), (AnimeA, "Anime", "tvshows")), ServerB()],
            [],
            Shows("Serie", _aShows, _bShows),
            Shows("Anime", _aAnime));

        Assert.False(report.SeriesSplit);
        Assert.All(report.Servers, server => Assert.Empty(server.SeriesSplitWith));
    }

    [Fact]
    public void LibrariesWithTheFoldersOfOneServerOnlySplitNothing()
    {
        var report = CheckTypeFolders(
            [ServerA((ShowsA, "Serie", "tvshows"), (AnimeA, "Anime", "tvshows")), ServerB()],
            [],
            Shows("Casa", _aShows, _aAnime),
            Shows("Casa 2", _aShows),
            Shows("Amico", _bShows));

        Assert.False(report.SeriesSplit);
    }

    [Fact]
    public void TheLibrariesWhoseOnlyPathsAreTypeFoldersOfTheServerAreNamed()
    {
        var report = CheckTypeFolders(
            [ServerA((FilmsA, "Film", "movies"), (ShowsA, "Serie", "tvshows"), (AnimeA, "Anime", "tvshows")), ServerB()],
            [],
            Library("Casa - Film", CollectionTypeOptions.movies, true, _aFilms),
            Shows("Casa - Serie", _aShows, _aAnime + "/"),
            Shows("Serie", _aShows, _bShows));

        Assert.Equal(["Casa - Film", "Casa - Serie"], LinkOfA(report).OnlyOnItsFolders);
        Assert.Empty(report.Servers[1].OnlyOnItsFolders);
    }

    [Fact]
    public void LibrariesThatCannotBeReadAreNotChecked()
    {
        var manager = new Mock<ILibraryManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetVirtualFolders()).Throws(new IOException("/secret/path"));
        var logger = new CapturingLogger<FederatedFolderLinkChecker>();

        var report = new FederatedFolderLinkChecker(manager.Object, new InMemoryConfigurationStore(), Mock.Of<IStubRegistry>(), logger).Check(Folder, [_a]);

        Assert.Equal(FederatedFolderLinkState.NotChecked, report.State);
        Assert.Contains(logger.Lines, line => line.Contains("/secret/path", StringComparison.Ordinal));
    }
}
