using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Index;

public class LocalCatalogReaderTests
{
    private readonly Mock<ILibraryManager> _library = new();
    private readonly InMemoryConfigurationStore _store = new();

    private LocalCatalogReader ReaderOver(params BaseItem[] items)
    {
        _library.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);
        return new LocalCatalogReader(_library.Object, _store);
    }

    private static Movie Film(string path) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Film",
        ProductionYear = 2020,
        Path = path,
        ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "1" }
    };

    [Fact]
    public void AFilmIsMapped()
    {
        var item = Assert.Single(ReaderOver(Film("/media/film/a.mkv")).Read());

        Assert.Equal(ContentKind.Movie, item.Kind);
        Assert.Equal("Film", item.Title);
        Assert.Equal(2020, item.Year);
        Assert.Equal("1", item.Ids.Tmdb);
    }

    [Fact]
    public void AFilmCarriesTheMetadataThatConfirmsAMatch()
    {
        var film = Film("/media/film/a.mkv");
        film.OriginalTitle = "Original";
        film.RunTimeTicks = 600;
        film.Overview = "Plot";
        film.Genres = ["Drama"];
        film.Studios = ["Studio"];

        var metadata = Assert.Single(ReaderOver(film).Read()).Metadata;

        Assert.Equal("Original", metadata.OriginalTitle);
        Assert.Equal(600, metadata.RunTimeTicks);
        Assert.Equal("Plot", metadata.Overview);
        Assert.Equal(["Drama"], metadata.Genres);
        Assert.Equal(["Studio"], metadata.Studios);
    }

    [Fact]
    public void PeopleAreReadOnlyWhenAComparisonAsks()
    {
        var film = Film("/media/film/a.mkv");
        _library.Setup(library => library.GetPeople(film)).Returns([new PersonInfo { Name = "Anna" }]);

        var metadata = Assert.Single(ReaderOver(film).Read()).Metadata;

        _library.Verify(library => library.GetPeople(It.IsAny<BaseItem>()), Times.Never);
        Assert.Equal(["Anna"], metadata.People.Value);
    }

    [Fact]
    public void AnEpisodeTakesTitleYearAndIdsFromItsSeries()
    {
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Show",
            ProductionYear = 2019,
            ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "7" }
        };
        _library.Setup(library => library.GetItemById(series.Id)).Returns(series);
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 2, IndexNumber = 3, Path = "/media/serie/e.mkv" };

        var item = Assert.Single(ReaderOver(episode).Read());

        Assert.Equal(ContentKind.Episode, item.Kind);
        Assert.Equal("Show", item.Title);
        Assert.Equal(2019, item.Year);
        Assert.Equal("7", item.Ids.Tvdb);
        Assert.Equal((2, 3), (item.SeasonNumber, item.EpisodeNumber));
        Assert.Equal(series.Id.ToString("N"), item.SeriesId);
    }

    [Fact]
    public void AFilmWithoutIdsThatJellyfinHasNotRefreshedAwaitsARefresh()
    {
        var film = Film("/media/film/a.mkv");
        film.ProviderIds = new Dictionary<string, string>();

        Assert.True(Assert.Single(ReaderOver(film).Read()).AwaitsRefresh);
    }

    [Fact]
    public void AFilmWithIdsAwaitsNoRefresh()
    {
        Assert.False(Assert.Single(ReaderOver(Film("/media/film/a.mkv")).Read()).AwaitsRefresh);
    }

    [Fact]
    public void AFilmRefreshedWithoutFindingIdsAwaitsNoRefresh()
    {
        var film = Film("/media/film/a.mkv");
        film.ProviderIds = new Dictionary<string, string>();
        film.DateLastRefreshed = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(Assert.Single(ReaderOver(film).Read()).AwaitsRefresh);
    }

    [Fact]
    public void AnEpisodeAwaitsNoRefresh()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        _library.Setup(library => library.GetItemById(series.Id)).Returns(series);
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, IndexNumber = 1, Path = "/media/serie/e.mkv" };

        Assert.False(Assert.Single(ReaderOver(episode).Read()).AwaitsRefresh);
    }

    [Fact]
    public void ALocalDoubleKeepsItsLastNumber()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        _library.Setup(library => library.GetItemById(series.Id)).Returns(series);
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2, Path = "/media/serie/e.mkv" };

        Assert.Equal(2, Assert.Single(ReaderOver(episode).Read()).EpisodeNumberEnd);
    }

    [Fact]
    public void ASeriesIsLookedUpOnce()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        _library.Setup(library => library.GetItemById(series.Id)).Returns(series);
        var episodes = Enumerable.Range(1, 3)
            .Select(number => (BaseItem)new Episode { Id = Guid.NewGuid(), SeriesId = series.Id, IndexNumber = number, Path = "/media/e.mkv" })
            .ToArray();

        ReaderOver(episodes).Read();

        _library.Verify(library => library.GetItemById(series.Id), Times.Once);
    }

    [Theory]
    [InlineData("/federata")]
    [InlineData("/federata/")]
    public void StubsUnderTheFederatedFolderAreLeftOut(string folder)
    {
        _store.Configuration.FederatedFolderPath = folder;

        var items = ReaderOver(Film("/federata/Film/a/a.strm"), Film("/media/film/a.mkv")).Read();

        Assert.Single(items);
    }

    [Fact]
    public void AFolderThatOnlySharesThePrefixIsNotTheFederatedFolder()
    {
        _store.Configuration.FederatedFolderPath = "/federata";

        Assert.Single(ReaderOver(Film("/federata2/Film/a.mkv")).Read());
    }

    [Fact]
    public void WithNoFederatedFolderNothingIsLeftOut()
    {
        Assert.Single(ReaderOver(Film("/federata/Film/a.strm")).Read());
    }

    [Fact]
    public void VirtualItemsAreLeftOut()
    {
        var missing = Film("/media/film/a.mkv");
        missing.IsVirtualItem = true;
        var noPath = Film(string.Empty);

        Assert.Empty(ReaderOver(missing, noPath).Read());
    }

    [Fact]
    public void TheQueryAsksForFilmsAndEpisodesWithoutVirtualItems()
    {
        InternalItemsQuery? query = null;
        _library.Setup(library => library.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(captured => query = captured)
            .Returns([]);

        new LocalCatalogReader(_library.Object, _store).Read();

        Assert.Equal([BaseItemKind.Movie, BaseItemKind.Episode], query!.IncludeItemTypes);
        Assert.True(query.Recursive);
        Assert.False(query.IsVirtualItem);
    }
}
