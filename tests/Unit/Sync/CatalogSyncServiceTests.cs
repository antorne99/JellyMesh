using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public sealed class CatalogSyncServiceTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private ServerCatalog CatalogOf(RemoteServerInfo server) => _fixture.Index.Snapshot.Find(server.Id)!;

    [Fact]
    public async Task ASyncFillsTheIndex()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(3));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);

        var run = await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(SyncStartResult.Started, run);
        var catalog = CatalogOf(server);
        Assert.Equal(ServerSyncState.Complete, catalog.State);
        Assert.Equal(3, catalog.Items.Count);
        Assert.Equal(SyncFixture.Start, catalog.LastSuccessUtc);
        Assert.All(catalog.Items, item => Assert.Equal(SyncFixture.Start, item.LastSeenUtc));
        Assert.True(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
    }

    [Fact]
    public async Task ASyncDropsTheCatalogOfARemovedServer()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(1));
        var removed = await _fixture.AddServerAsync("A", TestLibraries.Films);
        var kept = await _fixture.AddServerAsync("B", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        await _fixture.Sync.RemoveServerAsync(removed.Id, () => _fixture.Registry.RemoveAsync(removed.Id), CancellationToken.None);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Null(_fixture.Index.Snapshot.Find(removed.Id));
        Assert.NotNull(_fixture.Index.Snapshot.Find(kept.Id));
    }

    [Fact]
    public async Task ASyncKeepsTheCatalogOfAServerMissingFromTheConfiguration()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(1));
        var missing = await _fixture.AddServerAsync("A", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        await _fixture.Registry.RemoveAsync(missing.Id);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.NotNull(_fixture.Index.Snapshot.Find(missing.Id));
    }

    [Fact]
    public async Task ForgettingDropsTheCatalogOfAServerMissingFromTheConfiguration()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(1));
        var missing = await _fixture.AddServerAsync("A", TestLibraries.Films);
        var kept = await _fixture.AddServerAsync("B", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        await _fixture.Registry.RemoveAsync(missing.Id);

        Assert.Equal(1, await _fixture.Sync.ForgetMissingServersAsync(CancellationToken.None));
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Null(_fixture.Index.Snapshot.Find(missing.Id));
        Assert.NotNull(_fixture.Index.Snapshot.Find(kept.Id));
    }

    [Fact]
    public async Task ForgettingWithEveryServerConfiguredMarksNothing()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(1));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(0, await _fixture.Sync.ForgetMissingServersAsync(CancellationToken.None));
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.NotNull(_fixture.Index.Snapshot.Find(server.Id));
    }

    [Fact]
    public async Task AMarkOnAServerStillConfiguredKeepsItsCatalog()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(1));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        await _fixture.Sync.RemoveServerAsync(server.Id, () => Task.FromResult(true), CancellationToken.None);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.NotNull(_fixture.Index.Snapshot.Find(server.Id));
    }

    [Fact]
    public async Task AServerFailingInTheMiddleKeepsItsEntriesAndIsMarked()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize + 1));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var before = CatalogOf(server).Items;
        _fixture.Client.Queries.Clear();
        _fixture.Clock.Now = SyncFixture.Start.AddHours(1);

        // The first page answers and the second does not, so nothing of the first may land.
        _fixture.Client.Items[(TestLibraries.Films, RemoteItemType.Movie)].Clear();
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize + 1, "new"));
        _fixture.Client.OnPage = (query, _) => query.StartIndex > 0 ? RemoteFailure.Unreachable : RemoteFailure.None;
        await _fixture.Sync.RunAsync(CancellationToken.None);

        var catalog = CatalogOf(server);
        Assert.Same(before, catalog.Items);
        Assert.Equal(ServerSyncState.Stale, catalog.State);
        Assert.Equal(RemoteFailure.Unreachable, catalog.LastFailure);
        Assert.Equal(SyncFixture.Start.AddHours(1), catalog.LastAttemptUtc);
        Assert.Equal(SyncFixture.Start, catalog.LastSuccessUtc);
        Assert.False(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
    }

    [Fact]
    public async Task OneServerFailingDoesNotStopTheNext()
    {
        var bad = await _fixture.AddServerAsync("Bad", "deleted");
        var good = await _fixture.AddServerAsync("Good", TestLibraries.Films);
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(RemoteFailure.LibraryUnavailable, CatalogOf(bad).LastFailure);
        Assert.Empty(CatalogOf(bad).Items);
        Assert.False(_fixture.Index.Snapshot.IsAuthoritativeFor(bad.Id));
        Assert.Single(CatalogOf(good).Items);
        Assert.True(_fixture.Index.Snapshot.IsAuthoritativeFor(good.Id));
    }

    [Fact]
    public async Task ACompleteSyncAfterAFailureClearsTheMark()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        _fixture.Client.OnPage = (_, _) => RemoteFailure.Timeout;
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.OnPage = (_, _) => RemoteFailure.None;

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(ServerSyncState.Complete, CatalogOf(server).State);
        Assert.Null(CatalogOf(server).LastFailure);
    }

    [Fact]
    public async Task ACompleteReadReplacesTheEntriesOfTheServer()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("old"));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);

        _fixture.Client.Items[(TestLibraries.Films, RemoteItemType.Movie)].Clear();
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("new"));
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal("new", Assert.Single(CatalogOf(server).Items).ItemId);
    }

    [Fact]
    public async Task WhileSuspendedNothingStartsAndNothingChanges()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var before = _fixture.Index.Snapshot;
        _fixture.Client.Queries.Clear();
        _fixture.KeySource.Key = null;

        Assert.Equal(SyncStartResult.Suspended, _fixture.Sync.TryStart());
        Assert.Equal(SyncStartResult.Suspended, await _fixture.Sync.RunAsync(CancellationToken.None));

        Assert.Same(before, _fixture.Index.Snapshot);
        Assert.Empty(_fixture.Client.Queries);
        Assert.True(before.IsAuthoritativeFor(server.Id));
    }

    [Fact]
    public async Task WhileTheSettingsCannotBeReadNothingStartsAndNoServerIsForgotten()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var before = _fixture.Index.Snapshot;
        _fixture.Client.Queries.Clear();
        _fixture.Store.Unavailable = true;

        Assert.Equal(SyncStartResult.DatabaseSuspended, _fixture.Sync.TryStart());
        Assert.Equal(SyncStartResult.DatabaseSuspended, await _fixture.Sync.RunAsync(CancellationToken.None));
        Assert.Equal(SyncStartResult.DatabaseSuspended, await _fixture.Sync.RunIncrementalAsync(CancellationToken.None));
        await Assert.ThrowsAsync<StoreSuspendedException>(() => _fixture.Sync.ForgetMissingServersAsync(CancellationToken.None));

        _fixture.Store.Unavailable = false;
        Assert.Same(before, _fixture.Index.Snapshot);
        Assert.Empty(_fixture.Client.Queries);
        Assert.Equal(server.Id, Assert.Single(_fixture.Registry.GetServers()).Id);
    }

    [Fact]
    public async Task WhileTheDatabaseIsSuspendedNothingStartsAndNothingChanges()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.AddServerAsync("A", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var before = _fixture.Index.Snapshot;
        _fixture.Client.Queries.Clear();
        _fixture.Database.Suspended = true;

        Assert.Equal(SyncStartResult.DatabaseSuspended, _fixture.Sync.TryStart());
        Assert.Equal(SyncStartResult.DatabaseSuspended, await _fixture.Sync.RunIncrementalAsync(CancellationToken.None));

        Assert.Same(before, _fixture.Index.Snapshot);
        Assert.Empty(_fixture.Client.Queries);
        _fixture.Database.Suspended = false;
        Assert.Equal(SyncStartResult.Started, await _fixture.Sync.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASecondSyncIsRefusedWhileOneRuns()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.AddServerAsync("A", TestLibraries.Films);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Client.OnPage = (_, _) =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return RemoteFailure.None;
        };

        Assert.Equal(SyncStartResult.Started, _fixture.Sync.TryStart());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(_fixture.Sync.GetStatus().IsRunning);
        Assert.Equal(SyncStartResult.AlreadyRunning, _fixture.Sync.TryStart());
        Assert.Equal(SyncStartResult.AlreadyRunning, await _fixture.Sync.RunAsync(CancellationToken.None));

        release.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_fixture.Sync.GetStatus().IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        var status = _fixture.Sync.GetStatus();
        Assert.False(status.IsRunning);
        Assert.Equal(1, status.Servers[0].Movies);
        Assert.Equal(SyncStartResult.Started, await _fixture.Sync.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledSyncChangesNothingAndCanRunAgain()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.Sync.RunAsync(cancellation.Token));

        Assert.Null(_fixture.Index.Snapshot.Find(server.Id));
        Assert.Equal(SyncStartResult.Started, await _fixture.Sync.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnIndexThatCannotBeSavedIsReported()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        _fixture.Database.InterruptNextWrite();

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.True(_fixture.Sync.GetStatus().IndexSaveFailed);
        Assert.Null(_fixture.Index.Snapshot.Find(server.Id));
    }

    [Fact]
    public async Task TheStatusCountsByKindAndHoldsNoAddress()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        _fixture.Client.Add(TestLibraries.Shows, RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Client.Add(TestLibraries.Shows, RemoteItemType.Season, TestItems.Season("s1", "s", 1));
        _fixture.Client.Add(TestLibraries.Shows, RemoteItemType.Episode, TestItems.Episode("e1", "s", 1, 1), TestItems.Episode("e2", "s", 1, 2));
        await _fixture.AddServerAsync("Friend", TestLibraries.Films, TestLibraries.Shows);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var status = _fixture.Sync.GetStatus();
        var server = Assert.Single(status.Servers);
        Assert.Equal((1, 1, 2), (server.Movies, server.Series, server.Episodes));
        Assert.DoesNotContain(TestValues.Host, status.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStatusCountsTheNewTypes()
    {
        static RemoteItem Item(string id, string path) => new() { Id = id, Name = id, Path = path };
        _fixture.Client.Libraries.Add(new RemoteLibrary(TestLibraries.Music, "Musica", "music"));
        _fixture.Client.Libraries.Add(new RemoteLibrary(TestLibraries.MusicVideos, "Clip", "musicvideos"));
        _fixture.Client.Libraries.Add(new RemoteLibrary(TestLibraries.HomeVideos, "Casa", "homevideos"));
        _fixture.Client.Libraries.Add(new RemoteLibrary(TestLibraries.Books, "Libri", "books"));
        _fixture.Client.Add(TestLibraries.Music, RemoteItemType.Audio, Item("t1", "/music/1.flac"), Item("t2", "/music/2.flac"), Item("t3", "/music/3.flac"));
        _fixture.Client.Add(TestLibraries.MusicVideos, RemoteItemType.MusicVideo, Item("c1", "/clips/1.mkv"), Item("c2", "/clips/2.mkv"));
        _fixture.Client.Add(TestLibraries.HomeVideos, RemoteItemType.Video, Item("h1", "/home/1.mp4"));
        _fixture.Client.Add(TestLibraries.Books, RemoteItemType.Book, Item("b1", "/books/1.epub"));
        _fixture.Client.Add(TestLibraries.Books, RemoteItemType.AudioBook, Item("a1", "/books/a/1.mp3"), Item("a2", "/books/a/2.mp3"));
        await _fixture.AddServerAsync("Friend", TestLibraries.Music, TestLibraries.MusicVideos, TestLibraries.HomeVideos, TestLibraries.Books);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var server = Assert.Single(_fixture.Sync.GetStatus().Servers);
        Assert.Equal((3, 2, 1, 1, 2), (server.Tracks, server.MusicVideos, server.HomeVideos, server.Books, server.AudiobookFiles));
        Assert.Equal((0, 0, 0), (server.Movies, server.Series, server.Episodes));
    }

    [Fact]
    public async Task TheLogsHoldNoSecretAndNoAddress()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.OnPage = (_, _) => RemoteFailure.Unreachable;
        await _fixture.Sync.RunAsync(CancellationToken.None);

        IEnumerable<string> lines = _fixture.Logger.Lines.Concat(_fixture.ReaderLogger.Lines);

        Assert.NotEmpty(_fixture.Logger.Lines);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(TestValues.Token, line, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Password, line, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Host, line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AServerThatBreaksTheReadStopsOnlyItself()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(2));
        var first = await _fixture.AddServerAsync("A", TestLibraries.Films);
        var second = await _fixture.AddServerAsync("B", TestLibraries.Films);
        _fixture.Client.OnPage = (_, call) => call == 1 ? throw new InvalidOperationException("at http://remote.invalid:8096/x") : RemoteFailure.None;

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(ServerSyncState.Stale, CatalogOf(first).State);
        Assert.Equal(RemoteFailure.InvalidResponse, CatalogOf(first).LastFailure);
        Assert.Equal(ServerSyncState.Complete, CatalogOf(second).State);
        Assert.DoesNotContain(_fixture.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServerThatTakesForeverStopsAtItsLimitAndTheNextIsRead()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(2));
        var first = await _fixture.AddServerAsync("A", TestLibraries.Films);
        var second = await _fixture.AddServerAsync("B", TestLibraries.Films);
        _fixture.Client.BeforeClock = (read, token) => read == 1 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        _fixture.Sync.ServerReadLimit = TimeSpan.FromMilliseconds(200);

        await _fixture.Sync.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(RemoteFailure.Timeout, CatalogOf(first).LastFailure);
        Assert.Equal(ServerSyncState.Complete, CatalogOf(second).State);
    }

    [Fact]
    public async Task ASyncCancelledFromOutsideIsNotCountedAsATimeout()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movies(1));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films);
        using var cancelled = new CancellationTokenSource();
        _fixture.Client.BeforeClock = async (_, token) =>
        {
            await cancelled.CancelAsync();
            token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.Sync.RunAsync(cancelled.Token));

        Assert.Null(_fixture.Index.Snapshot.Find(server.Id)?.LastFailure);
    }
}
