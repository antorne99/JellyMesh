using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class StubRemoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellymesh-remover-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _library = new();
    private readonly StubRemover _remover;

    public StubRemoverTests()
    {
        _remover = new StubRemover(_library.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void ABatchRemovesEveryStubAndTheFoldersTheyLeaveEmpty()
    {
        var series = Series("show", "Show (2020)");
        var films = Enumerable.Range(1, 3).Select(i => Movie("m" + i, "Film " + i)).ToList();
        var episodes = new[] { Episode("e1", series, 1, 1), Episode("e2", series, 1, 2), Episode("e3", series, 2, 1) };
        var state = new StubState { Stubs = [.. films, .. episodes], Series = [series] };

        var removal = _remover.RemoveAll(_root, state, [.. films, .. episodes]);

        Assert.Empty(removal.State.Stubs);
        Assert.Empty(removal.State.Series);
        Assert.Equal(6, removal.Removed);
        Assert.Empty(removal.Failures);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, FederatedPaths.MovieFolder(string.Empty))));
        Assert.False(Directory.Exists(Path.Combine(_root, series.RelativePath)));
    }

    [Fact]
    public void ASeasonWithAnEpisodeLeftKeepsItsFolderAndTheSeries()
    {
        var series = Series("show", "Show (2020)");
        var gone = Episode("e1", series, 1, 1);
        var kept = Episode("e2", series, 1, 2);
        var state = new StubState { Stubs = [gone, kept], Series = [series] };

        var removal = _remover.RemoveAll(_root, state, [gone]);

        Assert.Equal(kept, Assert.Single(removal.State.Stubs));
        Assert.Equal(series, Assert.Single(removal.State.Series));
        Assert.False(File.Exists(Path.Combine(_root, gone.RelativePath)));
        Assert.True(File.Exists(Path.Combine(_root, kept.RelativePath)));
    }

    [Fact]
    public void AnEmptySeasonGoesWhileTheSeriesStays()
    {
        var series = Series("show", "Show (2020)");
        var gone = Episode("e1", series, 1, 1);
        var kept = Episode("e2", series, 2, 1);
        var season = Path.GetDirectoryName(Path.Combine(_root, gone.RelativePath))!;
        var seasonItem = new Folder { Id = Guid.NewGuid(), Path = season };
        _library.Setup(library => library.FindByPath(season, true)).Returns(seasonItem);

        var removal = _remover.RemoveAll(_root, new StubState { Stubs = [gone, kept], Series = [series] }, [gone]);

        Assert.False(Directory.Exists(season));
        Assert.Equal(series, Assert.Single(removal.State.Series));
        _library.Verify(library => library.DeleteItem(seasonItem, It.Is<DeleteOptions>(options => !options.DeleteFileLocation)), Times.Once);
    }

    [Fact]
    public void EachRemovedStubLosesItsLibraryRow()
    {
        var films = Enumerable.Range(1, 3).Select(i => Movie("m" + i, "Film " + i)).ToList();
        var items = films.ToDictionary(film => film.Id, film => new Movie { Id = Guid.NewGuid(), Path = Path.Combine(_root, film.RelativePath) });
        foreach (var film in films)
        {
            _library.Setup(library => library.FindByPath(Path.Combine(_root, film.RelativePath), false)).Returns(items[film.Id]);
        }

        _remover.RemoveAll(_root, new StubState { Stubs = films }, films);

        foreach (var item in items.Values)
        {
            _library.Verify(library => library.DeleteItem(item, It.IsAny<DeleteOptions>()), Times.Once);
        }
    }

    [Fact]
    public void AStubThatCannotBeRemovedStaysWhileTheOthersGo()
    {
        var films = Enumerable.Range(1, 3).Select(i => Movie("m" + i, "Film " + i)).ToList();
        var failing = Path.Combine(_root, films[1].RelativePath);
        _library.Setup(library => library.FindByPath(failing, false)).Throws(new IOException("busy"));

        var removal = _remover.RemoveAll(_root, new StubState { Stubs = films }, films);

        Assert.Equal(films[1], Assert.Single(removal.State.Stubs));
        Assert.Equal(2, removal.Removed);
        Assert.IsType<IOException>(Assert.Single(removal.Failures));
        Assert.True(File.Exists(failing));
    }

    [Fact]
    public void RemovingOneStubThrowsWhatStoppedIt()
    {
        var film = Movie("m1", "Film 1");
        _library.Setup(library => library.FindByPath(It.IsAny<string>(), false)).Throws(new IOException("busy"));

        Assert.Throws<IOException>(() => _remover.Remove(_root, new StubState { Stubs = [film] }, film));
    }

    [Fact]
    public void ALargeBatchStaysLinear()
    {
        var series = Enumerable.Range(0, 200).Select(i => Series("s" + i, "Show " + i)).ToList();
        var episodes = series.SelectMany(owner => Enumerable.Range(1, 100).Select(number => Episode(owner.Id + "e" + number, owner, 1, number, writeFile: false))).ToList();
        var state = new StubState { Stubs = episodes, Series = series };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var removal = _remover.RemoveAll(_root, state, episodes);

        Assert.Empty(removal.State.Stubs);
        Assert.Empty(removal.State.Series);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public void TheFolderOfARemovedServerGoesWithTheSeriesLeftInIt()
    {
        var server = Guid.NewGuid();
        var series = ServerSeries(server, "show");
        var seriesItem = new Folder { Id = Guid.NewGuid(), Path = Path.Combine(_root, series.RelativePath) };
        _library.Setup(library => library.FindByPath(seriesItem.Path, true)).Returns(seriesItem);
        Directory.CreateDirectory(Path.Combine(_root, FederatedPaths.MovieFolder(server, string.Empty)));
        File.WriteAllText(Path.Combine(_root, FederatedPaths.MovieFolder(server, string.Empty), FederatedPaths.HiddenFileName), string.Empty);

        var removal = _remover.RemoveServerFolders(_root, new StubState { Series = [series] }, [server], _ => false);

        Assert.False(Directory.Exists(Path.Combine(_root, FederatedPaths.ServerFolder(server))));
        Assert.Empty(removal.State.Series);
        Assert.Equal(1, removal.Removed);
        Assert.Empty(removal.Failures);
        _library.Verify(library => library.DeleteItem(seriesItem, It.Is<DeleteOptions>(options => !options.DeleteFileLocation)), Times.Once);
    }

    [Fact]
    public void AServerWithAStubLeftKeepsItsFolder()
    {
        var server = Guid.NewGuid();
        var stub = Movie("m1", "Film 1") with { ServerId = server, RelativePath = FederatedPaths.MovieStub(server, "Film 1", "A") };
        Write(stub.RelativePath);

        var removal = _remover.RemoveServerFolders(_root, new StubState { Stubs = [stub] }, [server], _ => false);

        Assert.True(File.Exists(Path.Combine(_root, stub.RelativePath)));
        Assert.Equal(stub, Assert.Single(removal.State.Stubs));
        Assert.Equal(0, removal.Removed);
    }

    [Fact]
    public void OnlyTheFoldersOfTheGivenServersGo()
    {
        var removed = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var series = ServerSeries(kept, "show");
        Directory.CreateDirectory(Path.Combine(_root, FederatedPaths.ServerFolder(removed), "Film"));
        var flat = Movie("m1", "Film 1");

        var removal = _remover.RemoveServerFolders(_root, new StubState { Stubs = [flat], Series = [series] }, [removed], _ => false);

        Assert.False(Directory.Exists(Path.Combine(_root, FederatedPaths.ServerFolder(removed))));
        Assert.True(Directory.Exists(Path.Combine(_root, series.RelativePath)));
        Assert.True(File.Exists(Path.Combine(_root, flat.RelativePath)));
        Assert.Equal(series, Assert.Single(removal.State.Series));
    }

    [Fact]
    public void ASeriesThatCannotBeRemovedKeepsTheFolderOfItsServer()
    {
        var server = Guid.NewGuid();
        var series = ServerSeries(server, "show");
        _library.Setup(library => library.FindByPath(Path.Combine(_root, series.RelativePath), true)).Throws(new IOException("busy"));

        var removal = _remover.RemoveServerFolders(_root, new StubState { Series = [series] }, [server], _ => false);

        Assert.True(Directory.Exists(Path.Combine(_root, series.RelativePath)));
        Assert.Equal(series, Assert.Single(removal.State.Series));
        Assert.IsType<IOException>(Assert.Single(removal.Failures));
        Assert.Equal(0, removal.Removed);
    }

    [Fact]
    public void AServerAddedBackWhileItsSeriesGoKeepsItsFolder()
    {
        var server = Guid.NewGuid();
        var series = ServerSeries(server, "show");
        var addedBack = false;
        _library.Setup(library => library.FindByPath(Path.Combine(_root, series.RelativePath), true)).Callback(() => addedBack = true).Returns((BaseItem?)null);
        File.WriteAllText(Path.Combine(_root, FederatedPaths.ServerFolder(server), FederatedPaths.HiddenFileName), string.Empty);

        var removal = _remover.RemoveServerFolders(_root, new StubState { Series = [series] }, [server], id => addedBack && id == server);

        Assert.True(Directory.Exists(Path.Combine(_root, FederatedPaths.ServerFolder(server))));
        Assert.Equal(0, removal.Removed);
    }

    [Fact]
    public void AServerAlreadyBackKeepsItsFolderAndItsSeries()
    {
        var server = Guid.NewGuid();
        var series = ServerSeries(server, "show");

        var removal = _remover.RemoveServerFolders(_root, new StubState { Series = [series] }, [server], id => id == server);

        Assert.True(Directory.Exists(Path.Combine(_root, series.RelativePath)));
        Assert.Equal(series, Assert.Single(removal.State.Series));
        Assert.Equal(0, removal.Removed);
    }

    [Fact]
    public void AFolderAlreadyGoneIsNoWork()
    {
        var removal = _remover.RemoveServerFolders(_root, StubState.Empty, [Guid.NewGuid()], _ => false);

        Assert.Equal(0, removal.Removed);
        Assert.Empty(removal.Failures);
    }

    private StubEntry Movie(string id, string title)
    {
        var path = FederatedPaths.MovieStub(title);
        Write(path);
        return new StubEntry
        {
            Id = id,
            Kind = StubKind.Movie,
            RelativePath = path,
            IdentityKey = id,
            Sources = []
        };
    }

    private SeriesStub Series(string id, string folder)
    {
        var path = FederatedPaths.SeriesFolderOf(folder);
        Directory.CreateDirectory(Path.Combine(_root, path));
        return new SeriesStub { Id = id, RelativePath = path, WorkKey = id };
    }

    private SeriesStub ServerSeries(Guid server, string id)
    {
        var path = FederatedPaths.SeriesFolderOf(server, "Show " + id);
        Directory.CreateDirectory(Path.Combine(_root, path));
        return new SeriesStub { Id = id, ServerId = server, RelativePath = path, WorkKey = id };
    }

    private StubEntry Episode(string id, SeriesStub series, int season, int number, bool writeFile = true)
    {
        var path = FederatedPaths.EpisodeStub(Path.GetFileName(series.RelativePath), "Show", season, number);
        if (writeFile)
        {
            Write(path);
        }

        return new StubEntry
        {
            Id = id,
            Kind = StubKind.Episode,
            RelativePath = path,
            IdentityKey = id,
            Sources = [],
            SeasonNumber = season,
            EpisodeNumber = number,
            SeriesStubId = series.Id
        };
    }

    private void Write(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "url");
        File.WriteAllText(Path.ChangeExtension(path, ".nfo"), "<nfo/>");
    }
}
