using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class FederatedFolderLinkCheckerTests
{
    private const string Folder = "/federata";

    private static VirtualFolderInfo Library(string name, CollectionTypeOptions? type, bool grouping, params string[] locations) => new()
    {
        Name = name,
        CollectionType = type,
        Locations = locations,
        LibraryOptions = new LibraryOptions { EnableAutomaticSeriesGrouping = grouping }
    };

    private static VirtualFolderInfo Movies(params string[] locations) => Library("Film", CollectionTypeOptions.movies, true, locations);

    private static VirtualFolderInfo Shows(params string[] locations) => Library("Serie", CollectionTypeOptions.tvshows, true, locations);

    // Strict: a call that is not this read would fail, so the test also proves the checker changes no library.
    private static FederatedFolderLinkReport Check(string folder, params VirtualFolderInfo[] libraries)
    {
        var manager = new Mock<ILibraryManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>(libraries));
        return new FederatedFolderLinkChecker(manager.Object, new CapturingLogger<FederatedFolderLinkChecker>()).Check(folder);
    }

    [Fact]
    public void BothPathsInLibrariesOfTheRightTypeAreLinked()
    {
        var report = Check(Folder, Movies("/film", "/federata/Film"), Shows("/federata/Serie", "/serie"));

        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
        Assert.Equal(FederatedPathLink.Linked, report.Movies.Link);
        Assert.Equal(["Film"], report.Movies.Libraries);
        Assert.Equal(FederatedPathLink.Linked, report.Series.Link);
        Assert.Empty(report.SeriesGroupingOff);
    }

    [Fact]
    public void NoLibraryAtAllIsNotLinked()
    {
        var report = Check(Folder);

        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
        Assert.Equal(FederatedPathLink.NotLinked, report.Movies.Link);
        Assert.Equal(FederatedPathLink.NotLinked, report.Series.Link);
    }

    [Fact]
    public void OnePathAloneIsNotEnough()
    {
        var report = Check(Folder, Movies("/federata/Film"));

        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
        Assert.Equal(FederatedPathLink.Linked, report.Movies.Link);
        Assert.Equal(FederatedPathLink.NotLinked, report.Series.Link);
    }

    [Theory]
    [InlineData("/federata", "/federata/Film/")]
    [InlineData("/federata/", "/federata/Film")]
    [InlineData("/federata/", "/federata/Film//")]
    [InlineData("/federata//", "/federata//Film/")]
    [InlineData("/federata", "/federata/./Film")]
    public void TrailingAndDoubledSeparatorsMakeNoDifference(string folder, string location)
    {
        Assert.Equal(FederatedPathLink.Linked, Check(folder, Movies(location)).Movies.Link);
    }

    [Theory]
    [InlineData("/federata/film")]
    [InlineData("/FEDERATA/Film")]
    public void OtherCapitalsAreAnotherFolderHereAndSaySo(string location)
    {
        var report = Check(Folder, Movies(location));

        Assert.Equal(FederatedPathLink.DifferentCase, report.Movies.Link);
        Assert.Equal(["Film"], report.Movies.Libraries);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void TheExactPathWinsOverOneWithOtherCapitals()
    {
        Assert.Equal(FederatedPathLink.Linked, Check(Folder, Movies("/federata/film", "/federata/Film")).Movies.Link);
    }

    [Fact]
    public void TheFilmPathInASeriesLibraryIsTheWrongType()
    {
        var report = Check(Folder, Library("Telefilm", CollectionTypeOptions.tvshows, true, "/federata/Film"));

        Assert.Equal(FederatedPathLink.WrongLibraryType, report.Movies.Link);
        Assert.Equal(["Telefilm"], report.Movies.Libraries);
        Assert.Equal(FederatedFolderLinkState.NotLinked, report.State);
    }

    [Fact]
    public void TheSeriesPathInAFilmLibraryIsTheWrongType()
    {
        Assert.Equal(FederatedPathLink.WrongLibraryType, Check(Folder, Movies("/federata/Serie")).Series.Link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CollectionTypeOptions.mixed)]
    [InlineData(CollectionTypeOptions.homevideos)]
    public void AMixedOrOtherLibraryIsTheWrongType(CollectionTypeOptions? type)
    {
        Assert.Equal(FederatedPathLink.WrongLibraryType, Check(Folder, Library("Altro", type, true, "/federata/Film")).Movies.Link);
    }

    [Fact]
    public void ALibraryOfTheWrongTypeIsReportedEvenBesideARightOne()
    {
        var report = Check(Folder, Movies("/federata/Film"), Library("Telefilm", CollectionTypeOptions.tvshows, true, "/federata/Film"));

        Assert.Equal(FederatedPathLink.WrongLibraryType, report.Movies.Link);
        Assert.Equal(["Telefilm"], report.Movies.Libraries);
    }

    [Theory]
    [InlineData("/federata")]
    [InlineData("/")]
    [InlineData("/federata/Film2")]
    [InlineData("/federata/Film/sub")]
    public void ANeighbourOfThePathDoesNotCount(string location)
    {
        Assert.Equal(FederatedPathLink.NotLinked, Check(Folder, Movies(location)).Movies.Link);
    }

    [Fact]
    public void ABlankLocationIsIgnored()
    {
        Assert.Equal(FederatedPathLink.NotLinked, Check(Folder, Movies(string.Empty, " ")).Movies.Link);
    }

    [Fact]
    public void GroupingOffOnTheLibraryOfTheSeriesPathIsReported()
    {
        var report = Check(Folder, Movies("/federata/Film"), Library("Serie", CollectionTypeOptions.tvshows, false, "/federata/Serie/"));

        Assert.Equal(["Serie"], report.SeriesGroupingOff);
        Assert.Equal(FederatedFolderLinkState.Linked, report.State);
    }

    [Fact]
    public void GroupingOffElsewhereIsNotReported()
    {
        var report = Check(Folder, Shows("/federata/Serie"), Library("Altre", CollectionTypeOptions.tvshows, false, "/altre"));

        Assert.Empty(report.SeriesGroupingOff);
    }

    [Fact]
    public void GroupingIsNotWhatIsWrongWithALibraryOfTheWrongType()
    {
        var report = Check(Folder, Library("Film", CollectionTypeOptions.movies, false, "/federata/Serie"));

        Assert.Empty(report.SeriesGroupingOff);
    }

    [Fact]
    public void LibrariesThatCannotBeReadAreNotChecked()
    {
        var manager = new Mock<ILibraryManager>(MockBehavior.Strict);
        manager.Setup(m => m.GetVirtualFolders()).Throws(new IOException("/secret/path"));
        var logger = new CapturingLogger<FederatedFolderLinkChecker>();

        var report = new FederatedFolderLinkChecker(manager.Object, logger).Check(Folder);

        Assert.Equal(FederatedFolderLinkState.NotChecked, report.State);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("/secret/path", System.StringComparison.Ordinal));
    }
}
