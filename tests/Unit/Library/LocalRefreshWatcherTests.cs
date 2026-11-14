using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class LocalRefreshWatcherTests : IDisposable
{
    private static readonly DateTime _stamped = new(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);

    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<ILocalRefreshRequester> _refreshes = new();
    private readonly Mock<ILocalTitleLinker> _linker = new();
    private readonly CapturingLogger<LocalRefreshWatcher> _logger = new();
    private readonly LocalRefreshWatcher _watcher;

    public LocalRefreshWatcherTests()
    {
        _linker.Setup(linker => linker.LinkAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _watcher = new LocalRefreshWatcher(_library.Object, _refreshes.Object, _linker.Object, _logger);
    }

    public void Dispose() => _watcher.Dispose();

    private void Updated(MediaBrowser.Controller.Entities.BaseItem item) =>
        _library.Raise(library => library.ItemUpdated += null, _library.Object, new ItemChangeEventArgs { Item = item, UpdateReason = ItemUpdateType.MetadataImport });

    [Fact]
    public async Task TheRefreshOfAFilmAskedForLinksItAtOnce()
    {
        var film = new Movie { Id = Guid.NewGuid(), DateLastRefreshed = _stamped };
        _refreshes.Setup(refreshes => refreshes.TryTakeArrival(film.Id)).Returns(true);
        await _watcher.StartAsync(CancellationToken.None);

        Updated(film);
        await _watcher.LastLink;

        _linker.Verify(linker => linker.LinkAsync(film.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheRefreshOfASeriesLinksTheSeries()
    {
        var series = new Series { Id = Guid.NewGuid(), DateLastRefreshed = _stamped };
        _refreshes.Setup(refreshes => refreshes.TryTakeArrival(series.Id)).Returns(true);
        await _watcher.StartAsync(CancellationToken.None);

        Updated(series);
        await _watcher.LastLink;

        _linker.Verify(linker => linker.LinkAsync(series.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ASaveBeforeTheRefreshIsNotItsArrival()
    {
        var film = new Movie { Id = Guid.NewGuid() };
        _refreshes.Setup(refreshes => refreshes.TryTakeArrival(film.Id)).Returns(true);
        await _watcher.StartAsync(CancellationToken.None);

        Updated(film);

        _refreshes.Verify(refreshes => refreshes.TryTakeArrival(It.IsAny<Guid>()), Times.Never);
        _linker.Verify(linker => linker.LinkAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnItemNotAskedForIsLeftToTheSync()
    {
        await _watcher.StartAsync(CancellationToken.None);

        Updated(new Movie { Id = Guid.NewGuid(), DateLastRefreshed = _stamped });

        _linker.Verify(linker => linker.LinkAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AFailedLinkIsLoggedByItsTypeOnly()
    {
        var film = new Movie { Id = Guid.NewGuid(), DateLastRefreshed = _stamped };
        _refreshes.Setup(refreshes => refreshes.TryTakeArrival(film.Id)).Returns(true);
        _linker.Setup(linker => linker.LinkAsync(film.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException(TestValues.Host));
        await _watcher.StartAsync(CancellationToken.None);

        Updated(film);
        await _watcher.LastLink;

        var line = Assert.Single(_logger.Lines);
        Assert.Contains("IOException", line, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterStoppingNothingIsWatched()
    {
        var film = new Movie { Id = Guid.NewGuid(), DateLastRefreshed = _stamped };
        _refreshes.Setup(refreshes => refreshes.TryTakeArrival(film.Id)).Returns(true);
        await _watcher.StartAsync(CancellationToken.None);
        await _watcher.StopAsync(CancellationToken.None);

        Updated(film);

        _linker.Verify(linker => linker.LinkAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
