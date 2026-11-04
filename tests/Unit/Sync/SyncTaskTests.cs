using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Model.Tasks;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public sealed class SyncTaskTests : IDisposable
{
    private readonly SyncFixture _fixture = new();
    private readonly CapturingLogger<IncrementalSyncTask> _incrementalLogger = new();
    private readonly CapturingLogger<ReconciliationTask> _reconciliationLogger = new();

    public void Dispose() => _fixture.Dispose();

    private static Progress<double> NoProgress => new();

    private IncrementalSyncTask Incremental(ICatalogSyncService? sync = null) => new(sync ?? _fixture.Sync, _fixture.Store, _incrementalLogger);

    private ReconciliationTask Reconciliation(ICatalogSyncService? sync = null) => new(sync ?? _fixture.Sync, _fixture.Store, _reconciliationLogger);

    [Fact]
    public void TheDefaultTriggersComeFromTheDefaultSettings()
    {
        var interval = Assert.Single(Incremental().GetDefaultTriggers());
        var daily = Assert.Single(Reconciliation().GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, interval.Type);
        Assert.Equal(TimeSpan.FromSeconds(900).Ticks, interval.IntervalTicks);
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, daily.Type);
        Assert.Equal(TimeSpan.FromHours(3).Ticks, daily.TimeOfDayTicks);
    }

    [Fact]
    public void TheDefaultTriggersFollowTheConfiguration()
    {
        _fixture.Store.Configuration.SyncIntervalSeconds = 60;
        _fixture.Store.Configuration.ReconciliationTime = "22:30";

        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, Assert.Single(Incremental().GetDefaultTriggers()).IntervalTicks);
        Assert.Equal(new TimeSpan(22, 30, 0).Ticks, Assert.Single(Reconciliation().GetDefaultTriggers()).TimeOfDayTicks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void AnIntervalThatMakesNoSenseGivesNoTrigger(int seconds)
    {
        _fixture.Store.Configuration.SyncIntervalSeconds = seconds;

        Assert.Empty(Incremental().GetDefaultTriggers());
    }

    [Fact]
    public void ATimeThatMakesNoSenseGivesNoTrigger()
    {
        _fixture.Store.Configuration.ReconciliationTime = "25:99";

        Assert.Empty(Reconciliation().GetDefaultTriggers());
    }

    [Fact]
    public void TheTwoTasksAreDistinctForJellyfin()
    {
        Assert.NotEqual(Incremental().Key, Reconciliation().Key);
        Assert.NotEqual(Incremental().Name, Reconciliation().Name);
        Assert.Equal(Incremental().Category, Reconciliation().Category);
    }

    [Fact]
    public async Task TheIncrementalTaskReadsChanges()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        var server = await _fixture.AddServerAsync("A", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Queries.Clear();
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("b"));
        _fixture.Client.SavedUtc["b"] = SyncFixture.Start;

        await Incremental().ExecuteAsync(NoProgress, CancellationToken.None);

        Assert.All(_fixture.Client.Queries, query => Assert.NotNull(query.ChangedSinceUtc));
        Assert.Equal(2, _fixture.Index.Snapshot.Find(server.Id)!.Items.Count);
    }

    [Fact]
    public async Task TheReconciliationTaskRemovesWhatIsGone()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("a"), TestItems.Movie("b"));
        var server = await _fixture.AddServerAsync("A", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Items[("films", RemoteItemType.Movie)].RemoveAt(0);

        await Reconciliation().ExecuteAsync(NoProgress, CancellationToken.None);

        Assert.Equal("b", Assert.Single(_fixture.Index.Snapshot.Find(server.Id)!.Items).ItemId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData(TestValues.OtherKey)]
    public async Task WithAnUnusableKeyBothTasksAreSuspended(string? key)
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        var server = await _fixture.AddServerAsync("A", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var before = _fixture.Index.Snapshot;
        var logins = _fixture.Client.Logins;
        var clockReads = _fixture.Client.ClockReads;
        _fixture.Client.Queries.Clear();
        _fixture.KeySource.Key = key;

        await Incremental().ExecuteAsync(NoProgress, CancellationToken.None);
        await Reconciliation().ExecuteAsync(NoProgress, CancellationToken.None);

        Assert.Empty(_fixture.Client.Queries);
        Assert.Equal(logins, _fixture.Client.Logins);
        Assert.Equal(clockReads, _fixture.Client.ClockReads);
        Assert.Same(before, _fixture.Index.Snapshot);
        Assert.True(before.IsAuthoritativeFor(server.Id));
        Assert.Contains(_incrementalLogger.Lines, line => line.Contains("suspended", StringComparison.Ordinal));
        Assert.Contains(_reconciliationLogger.Lines, line => line.Contains("suspended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithTheKeyBackTheTasksResume()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        var server = await _fixture.AddServerAsync("A", "films");
        _fixture.KeySource.Key = null;
        await Incremental().ExecuteAsync(NoProgress, CancellationToken.None);
        Assert.Null(_fixture.Index.Snapshot.Find(server.Id));

        _fixture.KeySource.Key = TestValues.Key;
        await Incremental().ExecuteAsync(NoProgress, CancellationToken.None);

        Assert.True(_fixture.Index.Snapshot.IsAuthoritativeFor(server.Id));
    }

    [Fact]
    public async Task APollInTheWayOfASyncIsSkippedNotQueued()
    {
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(s => s.RunIncrementalAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SyncStartResult.AlreadyRunning);

        await Incremental(sync.Object).ExecuteAsync(NoProgress, CancellationToken.None);

        sync.Verify(s => s.RunIncrementalAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheReconciliationWaitsForTheSyncInItsWay()
    {
        var sync = new Mock<ICatalogSyncService>();
        sync.SetupSequence(s => s.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(SyncStartResult.AlreadyRunning)
            .ReturnsAsync(SyncStartResult.AlreadyRunning)
            .ReturnsAsync(SyncStartResult.Started);
        var task = new ReconciliationTask(sync.Object, _fixture.Store, _reconciliationLogger) { RetryDelay = TimeSpan.FromMilliseconds(1) };

        await task.ExecuteAsync(NoProgress, CancellationToken.None);

        sync.Verify(s => s.RunAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task TheReconciliationStopsWaitingWhenCancelled()
    {
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(s => s.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(SyncStartResult.AlreadyRunning);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var task = new ReconciliationTask(sync.Object, _fixture.Store, _reconciliationLogger) { RetryDelay = TimeSpan.FromMilliseconds(1) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(NoProgress, cancellation.Token));
    }

    [Fact]
    public async Task WhatATaskThrowsCarriesNoAddress()
    {
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(s => s.RunIncrementalAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Cannot reach " + TestValues.Url));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Incremental(sync.Object).ExecuteAsync(NoProgress, CancellationToken.None));

        Assert.DoesNotContain(TestValues.Host, thrown.ToString(), StringComparison.Ordinal);
        Assert.All(_incrementalLogger.Lines, line => Assert.DoesNotContain(TestValues.Host, line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingATaskIsNotAFailure()
    {
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(s => s.RunIncrementalAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => Incremental(sync.Object).ExecuteAsync(NoProgress, CancellationToken.None));

        Assert.Empty(_incrementalLogger.Lines);
    }

    [Fact]
    public async Task TheLogsOfTheTasksHoldNoSecretAndNoAddress()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("a"));
        await _fixture.AddServerAsync("Friend", "films");
        await Incremental().ExecuteAsync(NoProgress, CancellationToken.None);
        _fixture.Client.ClockFailure = RemoteFailure.Unreachable;
        await Incremental().ExecuteAsync(NoProgress, CancellationToken.None);
        await Reconciliation().ExecuteAsync(NoProgress, CancellationToken.None);

        var lines = _fixture.Logger.Lines.Concat(_incrementalLogger.Lines).Concat(_reconciliationLogger.Lines).ToList();

        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(TestValues.Token, line, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Password, line, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Host, line, StringComparison.Ordinal);
        });
    }
}
