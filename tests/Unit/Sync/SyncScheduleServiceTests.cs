using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Model.Tasks;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

public sealed class SyncScheduleServiceTests : IDisposable
{
    private static readonly TaskResult _lastRun = new();

    private readonly InMemoryConfigurationStore _store = new();
    private readonly Mock<ITaskManager> _taskManager = new();
    private readonly CapturingLogger<SyncScheduleService> _logger = new();
    private readonly SyncScheduleService _service;

    public SyncScheduleServiceTests()
    {
        _service = new SyncScheduleService(_taskManager.Object, _store, _logger);
    }

    public void Dispose() => _service.Dispose();

    private Mock<IScheduledTaskWorker> Worker(IScheduledTask task, IReadOnlyList<TaskTriggerInfo> triggers, TaskResult? lastRun = null)
    {
        var worker = new Mock<IScheduledTaskWorker>();
        worker.SetupGet(w => w.ScheduledTask).Returns(task);
        worker.SetupGet(w => w.Name).Returns(task.Name);
        worker.SetupGet(w => w.LastExecutionResult).Returns(lastRun!);
        worker.SetupProperty(w => w.Triggers, triggers);
        return worker;
    }

    private IncrementalSyncTask IncrementalTask() => new(Mock.Of<ICatalogSyncService>(), _store, new CapturingLogger<IncrementalSyncTask>());

    private ReconciliationTask ReconciliationTask() => new(Mock.Of<ICatalogSyncService>(), _store, new CapturingLogger<ReconciliationTask>());

    private (Mock<IScheduledTaskWorker> Incremental, Mock<IScheduledTaskWorker> Reconciliation) WithTasks(TaskResult? lastRun)
    {
        var incremental = Worker(IncrementalTask(), SyncSchedule.IncrementalTriggers(_store.Configuration), lastRun);
        var reconciliation = Worker(ReconciliationTask(), SyncSchedule.ReconciliationTriggers(_store.Configuration));
        _taskManager.SetupGet(m => m.ScheduledTasks).Returns([incremental.Object, reconciliation.Object]);
        return (incremental, reconciliation);
    }

    // ExecuteAsync runs on its own thread, so it has to be over before a test changes what it reads.
    private async Task StartAndSettleAsync()
    {
        await _service.StartAsync(CancellationToken.None);
        await _service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void AssertSets(Mock<IScheduledTaskWorker> worker, Times times)
        => worker.VerifySet(w => w.Triggers = It.IsAny<IReadOnlyList<TaskTriggerInfo>>(), times);

    [Fact]
    public async Task ASavedIntervalReachesTheRunningTaskAtOnce()
    {
        var (incremental, reconciliation) = WithTasks(_lastRun);
        await StartAndSettleAsync();
        _store.Configuration.SyncIntervalSeconds = 60;

        _store.RaiseSettingsChanged();

        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, incremental.Object.Triggers.Single().IntervalTicks);
        AssertSets(reconciliation, Times.Never());
    }

    [Fact]
    public async Task ASavedTimeReachesTheRunningTaskAtOnce()
    {
        var (incremental, reconciliation) = WithTasks(_lastRun);
        await StartAndSettleAsync();
        _store.Configuration.ReconciliationTime = "22:30";

        _store.RaiseSettingsChanged();

        Assert.Equal(new TimeSpan(22, 30, 0).Ticks, reconciliation.Object.Triggers.Single().TimeOfDayTicks);
        AssertSets(incremental, Times.Never());
    }

    [Fact]
    public async Task ASaveOfAnotherSettingLeavesTheTimersAlone()
    {
        var (incremental, reconciliation) = WithTasks(_lastRun);
        await StartAndSettleAsync();
        _store.Configuration.MetadataProtection = false;

        _store.RaiseSettingsChanged();

        AssertSets(incremental, Times.Never());
        AssertSets(reconciliation, Times.Never());
    }

    [Fact]
    public async Task AScheduleEditedInTheDashboardGoesBackToTheConfigurationAtStart()
    {
        var incremental = Worker(IncrementalTask(), [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromSeconds(1).Ticks }], _lastRun);
        _taskManager.SetupGet(m => m.ScheduledTasks).Returns([incremental.Object]);

        await StartAndSettleAsync();

        Assert.Equal(TimeSpan.FromSeconds(900).Ticks, incremental.Object.Triggers.Single().IntervalTicks);
    }

    [Fact]
    public async Task ASaveBeforeTheTasksExistDoesNothingYet()
    {
        _taskManager.SetupGet(m => m.ScheduledTasks).Returns([]);
        await _service.StartAsync(CancellationToken.None);

        _store.RaiseSettingsChanged();

        Assert.Empty(_logger.Lines);
        await _service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheServiceWaitsForTheTasksJellyfinAddsAfterItStarts()
    {
        IReadOnlyList<IScheduledTaskWorker> tasks = [];
        _taskManager.SetupGet(m => m.ScheduledTasks).Returns(() => Volatile.Read(ref tasks));
        await _service.StartAsync(CancellationToken.None);
        Assert.False(_service.ExecuteTask!.IsCompleted);
        _store.Configuration.SyncIntervalSeconds = 60;

        var incremental = Worker(IncrementalTask(), [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = 1 }], _lastRun);
        Volatile.Write(ref tasks, [incremental.Object]);
        await _service.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromMinutes(1).Ticks, incremental.Object.Triggers.Single().IntervalTicks);
    }

    [Fact]
    public async Task ATaskThatNeverRanRunsOnceInsteadOfWaitingAnHour()
    {
        WithTasks(null);

        await StartAndSettleAsync();

        _taskManager.Verify(m => m.QueueScheduledTask<IncrementalSyncTask>(It.IsAny<TaskOptions>()), Times.Once);
    }

    [Fact]
    public async Task ATaskThatAlreadyRanIsLeftToItsTrigger()
    {
        WithTasks(_lastRun);

        await StartAndSettleAsync();

        _taskManager.Verify(m => m.QueueScheduledTask<IncrementalSyncTask>(It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task AScheduleThatCannotBeSavedDoesNotFailTheSave()
    {
        var (incremental, _) = WithTasks(_lastRun);
        incremental.SetupSet(w => w.Triggers = It.IsAny<IReadOnlyList<TaskTriggerInfo>>()).Throws(new IOException("/config/secret"));
        await StartAndSettleAsync();
        _store.Configuration.SyncIntervalSeconds = 60;

        _store.RaiseSettingsChanged();

        var line = Assert.Single(_logger.Lines);
        Assert.DoesNotContain("/config/secret", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureWhilePreparingNeverEscapesToTheHost()
    {
        _taskManager.SetupGet(m => m.ScheduledTasks).Throws(new InvalidOperationException("Cannot reach " + TestValues.Host));

        await StartAndSettleAsync();

        Assert.True(_service.ExecuteTask!.IsCompletedSuccessfully);
        Assert.DoesNotContain(TestValues.Host, Assert.Single(_logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterStoppingASaveChangesNothing()
    {
        var (incremental, _) = WithTasks(_lastRun);
        await StartAndSettleAsync();
        await _service.StopAsync(CancellationToken.None);
        _store.Configuration.SyncIntervalSeconds = 60;

        _store.RaiseSettingsChanged();

        AssertSets(incremental, Times.Never());
    }
}
