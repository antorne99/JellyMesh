using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class LibraryScansTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly FixedClock _clock = new(_start);
    private readonly Mock<ILibraryManager> _libraries = new();
    private readonly Mock<ITaskManager> _tasks = new();
    private readonly Mock<IScheduledTaskWorker> _worker = new();
    private readonly Queue<bool> _running = new();
    private readonly CapturingLogger<LibraryScans> _logger = new();
    private int _polls;

    public LibraryScansTests()
    {
        var task = new Mock<IScheduledTask>();
        task.SetupGet(scheduled => scheduled.Key).Returns("RefreshLibrary");
        _worker.SetupGet(worker => worker.ScheduledTask).Returns(task.Object);
        _worker.SetupGet(worker => worker.State).Returns(TaskState.Idle);
        _tasks.SetupGet(manager => manager.ScheduledTasks).Returns([_worker.Object]);
        _libraries.SetupGet(manager => manager.IsScanRunning).Returns(() => _running.Count > 0 ? _running.Dequeue() : false);
    }

    private LibraryScans Scans => new(_libraries.Object, _tasks.Object, _logger, _clock, (_, _) =>
    {
        _polls++;
        _clock.Now += TimeSpan.FromSeconds(1);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Waiting_for_idle_returns_once_the_running_scan_ended()
    {
        Enqueue(true, true, true, false);

        await Scans.WaitIdleAsync(CancellationToken.None);

        Assert.Equal(3, _polls);
        _libraries.Verify(manager => manager.QueueLibraryScan(), Times.Never);
    }

    [Fact]
    public async Task A_queued_scan_is_waited_for_until_it_ended()
    {
        _libraries.Setup(manager => manager.QueueLibraryScan()).Callback(() => Enqueue(false, true, true, false));

        await Scans.QueueAndWaitAsync(CancellationToken.None);

        _libraries.Verify(manager => manager.QueueLibraryScan(), Times.Once);
        Assert.Empty(_running);
    }

    [Fact]
    public async Task The_scan_is_queued_only_after_the_running_one_ended()
    {
        Enqueue(true, true, false);
        _libraries.Setup(manager => manager.QueueLibraryScan()).Callback(() =>
        {
            Assert.Empty(_running);
            Enqueue(true, false);
        });

        await Scans.QueueAndWaitAsync(CancellationToken.None);

        _libraries.Verify(manager => manager.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task A_scan_too_quick_to_be_seen_counts_as_ended_by_its_last_result()
    {
        _libraries.Setup(manager => manager.QueueLibraryScan()).Callback(() =>
            _worker.SetupGet(worker => worker.LastExecutionResult).Returns(new TaskResult { EndTimeUtc = _clock.Now.UtcDateTime.AddSeconds(1) }));

        await Scans.QueueAndWaitAsync(CancellationToken.None);

        Assert.True(_polls <= 1);
    }

    [Fact]
    public async Task An_old_result_does_not_count_as_the_queued_scan()
    {
        _worker.SetupGet(worker => worker.LastExecutionResult).Returns(new TaskResult { EndTimeUtc = _start.UtcDateTime.AddHours(-1) });
        _libraries.Setup(manager => manager.QueueLibraryScan()).Callback(() => Enqueue(false, false, true, false));

        await Scans.QueueAndWaitAsync(CancellationToken.None);

        Assert.Empty(_running);
    }

    [Fact]
    public async Task A_scan_that_never_starts_is_given_up_with_a_warning()
    {
        await Scans.QueueAndWaitAsync(CancellationToken.None);

        Assert.InRange(_polls, 30, 32);
        Assert.Contains(_logger.Lines, entry => entry.Contains("did not start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scan_that_never_ends_is_given_up_after_hours()
    {
        _libraries.SetupGet(manager => manager.IsScanRunning).Returns(true);

        await Scans.WaitIdleAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromHours(6), _clock.Now - _start);
    }

    private void Enqueue(params bool[] states)
    {
        foreach (var state in states)
        {
            _running.Enqueue(state);
        }
    }
}
