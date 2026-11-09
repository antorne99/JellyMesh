using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
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
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(3));
        var server = await _fixture.AddServerAsync("A", "films");

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
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(1));
        var removed = await _fixture.AddServerAsync("A", "films");
        var kept = await _fixture.AddServerAsync("B", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);
        await _fixture.Sync.MarkRemovedAsync(removed.Id, CancellationToken.None);
        _fixture.Registry.Remove(removed.Id);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Null(_fixture.Index.Snapshot.Find(removed.Id));
        Assert.NotNull(_fixture.Index.Snapshot.Find(kept.Id));
    }

    [Fact]
    public async Task ASyncKeepsTheCatalogOfAServerMissingFromTheConfiguration()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(1));
        var missing = await _fixture.AddServerAsync("A", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Registry.Remove(missing.Id);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.NotNull(_fixture.Index.Snapshot.Find(missing.Id));
    }

    [Fact]
    public async Task AMarkOnAServerStillConfiguredKeepsItsCatalog()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(1));
        var server = await _fixture.AddServerAsync("A", "films");
        await _fixture.Sync.MarkRemovedAsync(server.Id, CancellationToken.None);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.NotNull(_fixture.Index.Snapshot.Find(server.Id));
    }

    [Fact]
    public async Task AServerFailingInTheMiddleKeepsItsEntriesAndIsMarked()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize + 1));
        var server = await _fixture.AddServerAsync("A", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var before = CatalogOf(server).Items;
        _fixture.Client.Queries.Clear();
        _fixture.Clock.Now = SyncFixture.Start.AddHours(1);

        // The first page answers and the second does not, so nothing of the first may land.
        _fixture.Client.Items[("films", RemoteItemType.Movie)].Clear();
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(RemoteCatalogReader.PageSize + 1, "new"));
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
        var good = await _fixture.AddServerAsync("Good", "films");
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));

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
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
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
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("old"));
        var server = await _fixture.AddServerAsync("A", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);

        _fixture.Client.Items[("films", RemoteItemType.Movie)].Clear();
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("new"));
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal("new", Assert.Single(CatalogOf(server).Items).ItemId);
    }

    [Fact]
    public async Task WhileSuspendedNothingStartsAndNothingChanges()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
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
    public async Task ASecondSyncIsRefusedWhileOneRuns()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.AddServerAsync("A", "films");
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
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.Sync.RunAsync(cancellation.Token));

        Assert.Null(_fixture.Index.Snapshot.Find(server.Id));
        Assert.Equal(SyncStartResult.Started, await _fixture.Sync.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnIndexThatCannotBeSavedIsReported()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        var server = await _fixture.AddServerAsync("A", "films");
        await System.IO.File.WriteAllTextAsync(_fixture.Folder, "a file where the folder should be");

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.True(_fixture.Sync.GetStatus().IndexSaveFailed);
        Assert.Null(_fixture.Index.Snapshot.Find(server.Id));
        System.IO.File.Delete(_fixture.Folder);
    }

    [Fact]
    public async Task TheStatusCountsByKindAndHoldsNoAddress()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        _fixture.Client.Add("shows", RemoteItemType.Series, TestItems.Series("s"));
        _fixture.Client.Add("shows", RemoteItemType.Season, TestItems.Season("s1", "s", 1));
        _fixture.Client.Add("shows", RemoteItemType.Episode, TestItems.Episode("e1", "s", 1, 1), TestItems.Episode("e2", "s", 1, 2));
        await _fixture.AddServerAsync("Friend", "films", "shows");

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var status = _fixture.Sync.GetStatus();
        var server = Assert.Single(status.Servers);
        Assert.Equal((1, 1, 2), (server.Movies, server.Series, server.Episodes));
        Assert.DoesNotContain(TestValues.Host, status.ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLogsHoldNoSecretAndNoAddress()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("m"));
        await _fixture.AddServerAsync("Friend", "films");
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
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(2));
        var first = await _fixture.AddServerAsync("A", "films");
        var second = await _fixture.AddServerAsync("B", "films");
        _fixture.Client.OnPage = (_, call) => call == 1 ? throw new InvalidOperationException("at http://friend.tailnet.ts.net:8096/x") : RemoteFailure.None;

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(ServerSyncState.Stale, CatalogOf(first).State);
        Assert.Equal(RemoteFailure.InvalidResponse, CatalogOf(first).LastFailure);
        Assert.Equal(ServerSyncState.Complete, CatalogOf(second).State);
        Assert.DoesNotContain(_fixture.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServerThatTakesForeverStopsAtItsLimitAndTheNextIsRead()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(2));
        var first = await _fixture.AddServerAsync("A", "films");
        var second = await _fixture.AddServerAsync("B", "films");
        _fixture.Client.BeforeClock = (read, token) => read == 1 ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        _fixture.Sync.ServerReadLimit = TimeSpan.FromMilliseconds(200);

        await _fixture.Sync.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(RemoteFailure.Timeout, CatalogOf(first).LastFailure);
        Assert.Equal(ServerSyncState.Complete, CatalogOf(second).State);
    }

    [Fact]
    public async Task ASyncCancelledFromOutsideIsNotCountedAsATimeout()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movies(1));
        var server = await _fixture.AddServerAsync("A", "films");
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
