using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// Decision 48: each sync that runs leaves one entry in the history.
public sealed class SyncHistoryRecordingTests : IDisposable
{
    private readonly SyncFixture _fixture = new();
    private readonly RecordingHistory _history = new();

    public void Dispose() => _fixture.Dispose();

    private FakeRemoteClient Remote => _fixture.Client;

    private CatalogSyncService NewSync(ISyncHistory? history = null, ILibraryMaterializer? materializer = null)
    {
        return new CatalogSyncService(
            _fixture.Registry,
            _fixture.Reader,
            _fixture.Index,
            _fixture.Vault,
            _fixture.Clock,
            _fixture.Logger,
            materializer,
            stubs: _fixture.Stubs,
            history: history ?? _history);
    }

    private static Mock<ILibraryMaterializer> Materializer(MaterializationReport report)
    {
        var materializer = new Mock<ILibraryMaterializer>();
        materializer.Setup(item => item.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(report);
        return materializer;
    }

    private static MaterializationReport Report(int created, int updated, int removed, int failed, Dictionary<Guid, StubCounts> byServer)
    {
        return new MaterializationReport(SyncFixture.Start, true, created, updated, removed, failed, 0, []) { ByServer = byServer };
    }

    [Fact]
    public async Task AReconciliationIsRecordedWithWhatEachServerRead()
    {
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a", "Alfa"), TestItems.Movie("b", "Beta"));
        var server = await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        Remote.BeforeClock = (_, _) =>
        {
            _fixture.Clock.Now = _fixture.Clock.Now.AddSeconds(2);
            return Task.CompletedTask;
        };
        using var sync = NewSync();

        await sync.RunAsync(CancellationToken.None);

        var run = Assert.Single(_history.Runs);
        Assert.Equal(SyncFixture.Start, run.StartedUtc);
        Assert.Equal(2000, run.DurationMilliseconds);
        Assert.Equal(SyncRunKind.Reconciliation, run.Kind);
        Assert.Equal(SyncRunOutcome.Succeeded, run.Outcome);
        Assert.Equal(new SyncRunServer(server.Id, "Friend", 2, 0, 0, 0, 0, null), Assert.Single(run.Servers));
    }

    [Fact]
    public async Task AnIncrementalSyncIsRecordedWithWhatChanged()
    {
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a", "Alfa"), TestItems.Movie("b", "Beta"));
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        using var sync = NewSync();
        await sync.RunAsync(CancellationToken.None);
        _fixture.Clock.Now = _fixture.Clock.Now.AddMinutes(15);
        Remote.ServerTime = Remote.ServerTime!.Value.AddMinutes(15);
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("c", "Gamma"));
        Remote.SavedUtc["c"] = Remote.ServerTime.Value;

        await sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(2, _history.Runs.Count);
        var run = _history.Runs[0];
        Assert.Equal(SyncRunKind.Incremental, run.Kind);
        Assert.Equal(1, Assert.Single(run.Servers).Read);
    }

    [Fact]
    public async Task AStartFromThePageIsRecordedAsAReconciliation()
    {
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        using var sync = NewSync();

        Assert.Equal(SyncStartResult.Started, sync.TryStart());
        await WaitUntilAsync(() => _history.Runs.Count == 1);

        Assert.Equal(SyncRunKind.Reconciliation, _history.Runs[0].Kind);
    }

    [Fact]
    public async Task AServerThatCannotBeReadIsRecordedWithItsFailure_AndTheRunPartlyFailed()
    {
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a", "Alfa"));
        var first = await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        var second = await _fixture.AddServerAsync("Other", TestLibraries.Films);
        Remote.BeforeClock = (read, _) =>
        {
            Remote.ClockFailure = read == 2 ? RemoteFailure.Unreachable : RemoteFailure.None;
            return Task.CompletedTask;
        };
        using var sync = NewSync();

        await sync.RunAsync(CancellationToken.None);

        var run = Assert.Single(_history.Runs);
        Assert.Equal(SyncRunOutcome.PartlyFailed, run.Outcome);
        Assert.Equal(new SyncRunServer(first.Id, "Friend", 1, 0, 0, 0, 0, null), run.Servers[0]);
        Assert.Equal(new SyncRunServer(second.Id, "Other", 0, 0, 0, 0, 0, RemoteFailure.Unreachable), run.Servers[1]);
    }

    [Fact]
    public async Task NoServerRead_IsAFailedRun()
    {
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        Remote.ClockFailure = RemoteFailure.Timeout;
        using var sync = NewSync();

        await sync.RunAsync(CancellationToken.None);

        Assert.Equal(SyncRunOutcome.Failed, Assert.Single(_history.Runs).Outcome);
    }

    [Fact]
    public async Task TheStubsOfEachServerAndTheTotalsComeFromThisRunsMaterialization()
    {
        var server = await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        var report = Report(3, 2, 1, 0, new() { [server.Id] = new StubCounts(2, 1, 1, 0) });
        using var sync = NewSync(materializer: Materializer(report).Object);

        await sync.RunAsync(CancellationToken.None);

        var run = Assert.Single(_history.Runs);
        Assert.Equal((2, 1, 1, 0), (run.Servers[0].Created, run.Servers[0].Updated, run.Servers[0].Removed, run.Servers[0].Failed));
        Assert.Equal((3, 2, 1, 0), (run.Created, run.Updated, run.Removed, run.Failed));
        Assert.Equal(SyncRunOutcome.Succeeded, run.Outcome);
    }

    [Fact]
    public async Task StubsThatCouldNotBeWritten_MakeTheRunPartlyFailed()
    {
        var server = await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        var report = Report(0, 0, 0, 1, new() { [server.Id] = new StubCounts(0, 0, 0, 1) });
        using var sync = NewSync(materializer: Materializer(report).Object);

        await sync.RunAsync(CancellationToken.None);

        var run = Assert.Single(_history.Runs);
        Assert.Equal(SyncRunOutcome.PartlyFailed, run.Outcome);
        Assert.Equal(1, run.Servers[0].Failed);
    }

    [Fact]
    public async Task AMaterializationThatThrows_IsAFailedRun()
    {
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        var materializer = new Mock<ILibraryMaterializer>();
        materializer.Setup(item => item.MaterializeAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException(TestValues.Host));
        using var sync = NewSync(materializer: materializer.Object);

        await sync.RunAsync(CancellationToken.None);

        var run = Assert.Single(_history.Runs);
        Assert.Equal(SyncRunOutcome.Failed, run.Outcome);
        Assert.True(run.MaterializationFailed);
    }

    [Fact]
    public async Task ACancelledRunIsRecorded()
    {
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        using var cancel = new CancellationTokenSource();
        Remote.BeforeClock = async (_, token) =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
        };
        using var sync = NewSync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.RunAsync(cancel.Token));

        Assert.Equal(SyncRunOutcome.Cancelled, Assert.Single(_history.Runs).Outcome);
        Assert.False(sync.GetStatus().IsRunning);
    }

    [Fact]
    public async Task ALibraryThatVanishesIsAWarningOfItsServerInThatRunOnly()
    {
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a", "Alfa"));
        Remote.Add(TestLibraries.FilmsB, RemoteItemType.Movie, TestItems.Movie("b", "Beta", 2021, "2"));
        Remote.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsB, "Film 4K", "movies"));
        await _fixture.AddServerAsync("Friend", TestLibraries.Films, TestLibraries.FilmsB);
        using var sync = NewSync();
        await sync.RunAsync(CancellationToken.None);
        Remote.Libraries.RemoveAll(library => library.Id == TestLibraries.FilmsB);

        await sync.RunAsync(CancellationToken.None);
        Assert.Null(Assert.Single(_history.Runs[0].Servers).Warning);
        await sync.RunAsync(CancellationToken.None);
        await sync.RunAsync(CancellationToken.None);

        Assert.Equal(["Film 4K"], Assert.Single(_history.Runs[1].Servers).Warning!.VanishedLibraries);
        Assert.Null(Assert.Single(_history.Runs[1].Servers).Failure);
        Assert.Null(Assert.Single(_history.Runs[0].Servers).Warning);
    }

    [Fact]
    public async Task TheRunIsInTheHistoryBeforeTheSyncShowsFinished()
    {
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        bool? runningWhenAdded = null;
        CatalogSyncService? sync = null;
        var history = new Mock<ISyncHistory>();
        history.Setup(item => item.AddAsync(It.IsAny<SyncRun>(), It.IsAny<CancellationToken>()))
            .Callback(() => runningWhenAdded = sync!.GetStatus().IsRunning)
            .Returns(Task.CompletedTask);
        using (sync = NewSync(history.Object))
        {
            await sync.RunAsync(CancellationToken.None);
        }

        Assert.True(runningWhenAdded);
    }

    [Fact]
    public async Task AHistoryThatCannotBeSaved_NeverFailsTheSync_AndLogsTheWholeError()
    {
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a", "Alfa"));
        var server = await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        var history = new Mock<ISyncHistory>();
        history.Setup(item => item.AddAsync(It.IsAny<SyncRun>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException(TestValues.Host));
        using var sync = NewSync(history.Object);

        Assert.Equal(SyncStartResult.Started, await sync.RunAsync(CancellationToken.None));
        Assert.Equal(SyncStartResult.Started, await sync.RunIncrementalAsync(CancellationToken.None));

        Assert.False(sync.GetStatus().IsRunning);
        Assert.Single(_fixture.Index.Snapshot.Find(server.Id)!.Items);
        Assert.Contains(_fixture.Logger.Lines, line => line.Contains(nameof(IOException), StringComparison.Ordinal));
        Assert.Contains(_fixture.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSavedHistoryHoldsNoAddressNoTokenAndNoLogin()
    {
        Remote.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("a", "Alfa"));
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        await _fixture.AddServerAsync("Other", TestLibraries.Films);
        Remote.BeforeClock = (read, _) => read == 2 ? throw new HttpRequestException(TestValues.Url) : Task.CompletedTask;
        using var database = new TestDatabase();
        using var store = new SyncHistoryStore(database, new CapturingLogger<SyncHistoryStore>());
        using var sync = NewSync(store);

        await sync.RunAsync(CancellationToken.None);

        var file = Path.Combine(database.Folder, PluginDatabase.FileName);
        var content = await File.ReadAllTextAsync(file) + await File.ReadAllTextAsync(file + "-wal");
        Assert.Contains("InvalidResponse", content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Password, content, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Username, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASyncWithNoServerConfigured_IsNotRecorded()
    {
        using var sync = NewSync();

        await sync.RunAsync(CancellationToken.None);
        await sync.RunIncrementalAsync(CancellationToken.None);
        Assert.Equal(SyncStartResult.Started, sync.TryStart());
        await WaitUntilAsync(() => !sync.GetStatus().IsRunning);

        Assert.Empty(_history.Runs);
    }

    [Fact]
    public async Task ASyncThatDoesNotRunIsNotRecorded()
    {
        await _fixture.AddServerAsync("Friend", TestLibraries.Films);
        _fixture.KeySource.Key = null;
        using var sync = NewSync();

        Assert.Equal(SyncStartResult.Suspended, await sync.RunAsync(CancellationToken.None));

        Assert.Empty(_history.Runs);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private sealed class RecordingHistory : ISyncHistory
    {
        private readonly List<SyncRun> _runs = [];

        public IReadOnlyList<SyncRun> Runs => _runs;

        public bool IsDamaged => false;

        public Task AddAsync(SyncRun run, CancellationToken cancellationToken)
        {
            _runs.Insert(0, run);
            return Task.CompletedTask;
        }
    }
}
