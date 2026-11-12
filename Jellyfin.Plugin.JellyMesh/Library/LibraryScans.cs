using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Waits for the library scans of the local server by polling it, since it raises no event for them.
/// </summary>
public sealed class LibraryScans : ILibraryScans
{
    private const string ScanTaskKey = "RefreshLibrary";
    private static readonly TimeSpan _poll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _startLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _runLimit = TimeSpan.FromHours(6);

    private readonly ILibraryManager _libraries;
    private readonly ITaskManager _tasks;
    private readonly ILogger<LibraryScans> _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScans"/> class.
    /// </summary>
    /// <param name="libraries">The library manager.</param>
    /// <param name="tasks">The task manager.</param>
    /// <param name="logger">The logger.</param>
    public LibraryScans(ILibraryManager libraries, ITaskManager tasks, ILogger<LibraryScans> logger)
        : this(libraries, tasks, logger, TimeProvider.System, Task.Delay)
    {
    }

    internal LibraryScans(ILibraryManager libraries, ITaskManager tasks, ILogger<LibraryScans> logger, TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _libraries = libraries;
        _tasks = tasks;
        _logger = logger;
        _time = time;
        _delay = delay;
    }

    /// <inheritdoc />
    public async Task WaitIdleAsync(CancellationToken cancellationToken)
    {
        var start = _time.GetUtcNow();
        while (IsRunning(Worker()))
        {
            if (_time.GetUtcNow() - start >= _runLimit)
            {
                _logger.LogWarning("A library scan is still running after {Hours} hours, so JellyMesh goes on", _runLimit.TotalHours);
                return;
            }

            await _delay(_poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task QueueAndWaitAsync(CancellationToken cancellationToken)
    {
        await WaitIdleAsync(cancellationToken).ConfigureAwait(false);
        var queued = _time.GetUtcNow();
        _libraries.QueueLibraryScan();
        var seen = false;
        while (true)
        {
            var worker = Worker();
            var elapsed = _time.GetUtcNow() - queued;
            if (IsRunning(worker))
            {
                seen = true;
                if (elapsed >= _runLimit)
                {
                    _logger.LogWarning("The library scan is still running after {Hours} hours, so JellyMesh goes on", _runLimit.TotalHours);
                    return;
                }
            }
            else if (seen || worker?.LastExecutionResult?.EndTimeUtc >= queued.UtcDateTime)
            {
                return;
            }
            else if (elapsed >= _startLimit)
            {
                _logger.LogWarning("The library scan JellyMesh queued did not start within {Seconds} seconds", _startLimit.TotalSeconds);
                return;
            }

            await _delay(_poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitReadyAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        var start = _time.GetUtcNow();
        while (!IsReady())
        {
            if (_time.GetUtcNow() - start >= limit)
            {
                return false;
            }

            await _delay(_poll, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // A library renamed without a refresh, or new, gets its id only from a scan.
    private bool IsReady()
    {
        if (IsRunning(Worker()))
        {
            return false;
        }

        try
        {
            return _libraries.GetVirtualFolders().All(folder => !string.IsNullOrEmpty(folder.ItemId));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private IScheduledTaskWorker? Worker() =>
        _tasks.ScheduledTasks.FirstOrDefault(worker => string.Equals(worker.ScheduledTask.Key, ScanTaskKey, StringComparison.Ordinal));

    private bool IsRunning(IScheduledTaskWorker? worker) => _libraries.IsScanRunning || worker?.State == TaskState.Running;
}
