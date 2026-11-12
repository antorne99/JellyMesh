using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The page shows, for each server, how its two folders are linked, and warns about libraries that break the links (Q22,
// Q45, Q46, Q53, D13).
public sealed class FederatedFolderLinkCheckerTests
{
    private const string Folder = "/federata";
    private static readonly Guid _a = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid _b = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly string _aFilm = $"/federata/{_a:D}/Film";
    private static readonly string _aSerie = $"/federata/{_a:D}/Serie";
    private static readonly string _bFilm = $"/federata/{_b:D}/Film";
    private static readonly string _bSerie = $"/federata/{_b:D}/Serie";

    private static VirtualFolderInfo Library(string name, CollectionTypeOptions? type, bool grouping, params string[] locations) => new()
    {
        Name = name,
        CollectionType = type,
        Locations = locations,
        LibraryOptions = new LibraryOptions { EnableAutomaticSeriesGrouping = grouping }
    };

    private static VirtualFolderInfo Movies(params string[] locations) => Library("Film", CollectionTypeOptions.movies, true, locations);

    private static VirtualFolderInfo Shows(string name, params string[] locations) => Library(name, CollectionTypeOptions.tvshows, true, locations);

    // Strict: a call that is not this read would fail, so the test also proves the checker changes no library.
    private static FederatedFolderLinkReport Check(string folder, Guid[] servers, params VirtualFolderInfo[] libraries)
    {
        var manager = new Mock<ILibraryManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>(libraries));
        return new FederatedFolderLinkChecker(manager.Object, new CapturingLogger<FederatedFolderLinkChecker>()).Check(folder, servers);
    }

    private static FederatedFolderLinkReport Check(params VirtualFolderInfo[] libraries) => Check(Folder, [_a], libraries);

    private static ServerFolderLink ServerA(FederatedFolderLinkReport report) => report.Servers.Single(server => server.ServerId == _a);

    [Fact]
    public void EachServerHasItsTwoPaths()
    {
        var report = Check(Folder, [_a, _b]);

        Assert.Equal([_a, _b], report.Servers.Select(server => server.ServerId));
        Assert.Equal(_aFilm, ServerA(report).MoviesPath);
        Assert.Equal(_aSerie, ServerA(report).SeriesPath);
        Assert.Equal(_bSerie, report.Servers[1].SeriesPath);
    }

    [Fact]
    public void BothPathsInLibrariesOfTheRightTypeAreLinked()
    {
        var report = Check(Movies("/film", _aFilm), Shows("Serie", _aSerie, "/serie"));

        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
        Assert.Equal(FederatedPathLink.Linked, ServerA(report).Movies.Link);
        Assert.Equal(["Film"], ServerA(report).Movies.Libraries);
        Assert.Equal(FederatedPathLink.Linked, ServerA(report).Series.Link);
        Assert.Empty(ServerA(report).SeriesGroupingOff);
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
        Assert.Equal(FederatedPathLink.NotLinked, ServerA(report).Movies.Link);
        Assert.Equal(FederatedPathLink.NotLinked, ServerA(report).Series.Link);
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
        Assert.Equal(FederatedPathLink.NotLinked, ServerA(report).Movies.Link);
        Assert.Equal(FederatedPathLink.NotLinked, ServerA(report).Series.Link);
    }

    [Fact]
    public void OnePathAloneIsNotEnough()
    {
        var report = Check(Movies(_aFilm));

        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
        Assert.Equal(FederatedPathLink.Linked, ServerA(report).Movies.Link);
        Assert.Equal(FederatedPathLink.NotLinked, ServerA(report).Series.Link);
    }

    [Theory]
    [InlineData("/federata", "{0}/")]
    [InlineData("/federata/", "{0}")]
    [InlineData("/federata/", "{0}//")]
    [InlineData("/federata//", "/federata//aaaaaaaa-0000-4000-8000-000000000001/Film/")]
    [InlineData("/federata", "/federata/./aaaaaaaa-0000-4000-8000-000000000001/Film")]
    public void TrailingAndDoubledSeparatorsMakeNoDifference(string folder, string location)
    {
        Assert.Equal(FederatedPathLink.Linked, ServerA(Check(folder, [_a], Movies(string.Format(System.Globalization.CultureInfo.InvariantCulture, location, _aFilm)))).Movies.Link);
    }

    [Theory]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001/film")]
    [InlineData("/FEDERATA/aaaaaaaa-0000-4000-8000-000000000001/Film")]
    [InlineData("/federata/AAAAAAAA-0000-4000-8000-000000000001/Film")]
    public void OtherCapitalsAreAnotherFolderHereAndSaySo(string location)
    {
        var report = Check(Movies(location));

        Assert.Equal(FederatedPathLink.DifferentCase, ServerA(report).Movies.Link);
        Assert.Equal(["Film"], ServerA(report).Movies.Libraries);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void TheExactPathWinsOverOneWithOtherCapitals()
    {
        Assert.Equal(FederatedPathLink.Linked, ServerA(Check(Movies(_aFilm.ToLowerInvariant(), _aFilm))).Movies.Link);
    }

    [Fact]
    public void TheFilmPathInASeriesLibraryIsTheWrongType()
    {
        var report = Check(Library("Telefilm", CollectionTypeOptions.tvshows, true, _aFilm));

        Assert.Equal(FederatedPathLink.WrongLibraryType, ServerA(report).Movies.Link);
        Assert.Equal(["Telefilm"], ServerA(report).Movies.Libraries);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void TheSeriesPathInAFilmLibraryIsTheWrongType()
    {
        Assert.Equal(FederatedPathLink.WrongLibraryType, ServerA(Check(Movies(_aSerie))).Series.Link);
    }

    [Theory]
    [InlineData(CollectionTypeOptions.homevideos)]
    [InlineData(CollectionTypeOptions.music)]
    public void AnotherLibraryTypeIsTheWrongType(CollectionTypeOptions type)
    {
        Assert.Equal(FederatedPathLink.WrongLibraryType, ServerA(Check(Library("Altro", type, true, _aFilm))).Movies.Link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CollectionTypeOptions.mixed)]
    public void BothPathsInMixedLibrariesAreLinked(CollectionTypeOptions? type)
    {
        var report = Check(Library("PuppyNas", type, true, _aFilm, _aSerie));

        Assert.Equal(FederatedPathLink.Linked, ServerA(report).Movies.Link);
        Assert.Equal(["PuppyNas"], ServerA(report).Movies.Libraries);
        Assert.Equal(FederatedPathLink.Linked, ServerA(report).Series.Link);
        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
    }

    [Fact]
    public void ALibraryOfTheWrongTypeIsReportedEvenBesideARightOne()
    {
        var report = Check(Movies(_aFilm), Library("Telefilm", CollectionTypeOptions.tvshows, true, _aFilm));

        Assert.Equal(FederatedPathLink.WrongLibraryType, ServerA(report).Movies.Link);
        Assert.Equal(["Telefilm"], ServerA(report).Movies.Libraries);
    }

    [Theory]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001/Film2")]
    [InlineData("/federata/aaaaaaaa-0000-4000-8000-000000000001/Film/sub")]
    [InlineData("/federata/Film")]
    public void ANeighbourOfThePathDoesNotCount(string location)
    {
        Assert.Equal(FederatedPathLink.NotLinked, ServerA(Check(Movies(location))).Movies.Link);
    }

    [Fact]
    public void ABlankLocationIsIgnored()
    {
        var report = Check(Movies(string.Empty, " "));

        Assert.Equal(FederatedPathLink.NotLinked, ServerA(report).Movies.Link);
        Assert.Empty(report.Covering);
    }

    [Fact]
    public void GroupingOffOnTheLibraryOfTheSeriesPathIsReportedForThatServer()
    {
        var report = Check(Folder, [_a, _b], Movies(_aFilm, _bFilm), Library("Serie", CollectionTypeOptions.tvshows, false, _aSerie + "/"), Shows("Altre", _bSerie));

        Assert.Equal(["Serie"], ServerA(report).SeriesGroupingOff);
        Assert.Empty(report.Servers[1].SeriesGroupingOff);
        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
    }

    [Fact]
    public void GroupingOffElsewhereIsNotReported()
    {
        var report = Check(Shows("Serie", _aSerie), Library("Altre", CollectionTypeOptions.tvshows, false, "/altre"));

        Assert.Empty(ServerA(report).SeriesGroupingOff);
    }

    [Fact]
    public void GroupingIsNotWhatIsWrongWithALibraryOfTheWrongType()
    {
        var report = Check(Library("Film", CollectionTypeOptions.movies, false, _aSerie));

        Assert.Empty(ServerA(report).SeriesGroupingOff);
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
        Assert.Equal(["Serie", "Tutte"], ServerA(report).SeriesLibraries);
    }

    [Fact]
    public void AServerSeriesFolderInOneMoreLibrarySplitsTheSeries()
    {
        var report = Check(Folder, [_a, _b], Shows("Serie", _aSerie, _bSerie), Shows("Casa - Serie", _aSerie));

        Assert.True(report.SeriesSplit);
        Assert.Equal(["Serie", "Casa - Serie"], ServerA(report).SeriesLibraries);
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

        Assert.Equal(["Casa - Film", "Casa - Serie"], ServerA(report).OnlyOnItsFolders);
    }

    [Fact]
    public void LibrariesThatCannotBeReadAreNotChecked()
    {
        var manager = new Mock<ILibraryManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetVirtualFolders()).Throws(new IOException("/secret/path"));
        var logger = new CapturingLogger<FederatedFolderLinkChecker>();

        var report = new FederatedFolderLinkChecker(manager.Object, logger).Check(Folder, [_a]);

        Assert.Equal(FederatedFolderLinkState.NotChecked, report.State);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("/secret/path", StringComparison.Ordinal));
    }
}
