using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Keeps the triggers of the sync tasks equal to the configuration. Jellyfin reads <c>GetDefaultTriggers</c> only while no
/// saved trigger file exists, so the only way to change a schedule without a restart is the setter of the task worker.
/// </summary>
public sealed class SyncScheduleService : BackgroundService
{
    private static readonly TimeSpan _taskLookupDelay = TimeSpan.FromSeconds(1);

    private readonly ITaskManager _taskManager;
    private readonly IPluginConfigurationStore _store;
    private readonly ILogger<SyncScheduleService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncScheduleService"/> class.
    /// </summary>
    /// <param name="taskManager">The task manager.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    public SyncScheduleService(ITaskManager taskManager, IPluginConfigurationStore store, ILogger<SyncScheduleService> logger)
    {
        _taskManager = taskManager;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _store.SettingsChanged += OnSettingsChanged;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _store.SettingsChanged -= OnSettingsChanged;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Sets the triggers of the tasks to the configured ones, if they differ. Setting a trigger restarts its count, so a
    /// save of an unrelated setting must leave it alone or it would put the next sync off.
    /// </summary>
    internal void ApplySchedule()
    {
        var (incremental, reconciliation) = _store.Read(config => (SyncSchedule.IncrementalTriggers(config), SyncSchedule.ReconciliationTriggers(config)));
        Apply<IncrementalSyncTask>(incremental);
        Apply<ReconciliationTask>(reconciliation);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await PrepareTasksAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // What escapes a hosted service stops the whole server, and a missed schedule is not worth that.
            _logger.LogError("Cannot prepare the sync schedule: {Error}", exception.GetType().Name);
        }
    }

    private static bool AreSame(IReadOnlyList<TaskTriggerInfo> first, IReadOnlyList<TaskTriggerInfo> second)
    {
        static (TaskTriggerInfoType, long?, long?) KeyOf(TaskTriggerInfo trigger) => (trigger.Type, trigger.IntervalTicks, trigger.TimeOfDayTicks);

        return first.Select(KeyOf).SequenceEqual(second.Select(KeyOf));
    }

    private async Task PrepareTasksAsync(CancellationToken cancellationToken)
    {
        // Jellyfin starts hosted services first and adds the scheduled tasks after.
        while (WorkerOf<IncrementalSyncTask>() is null)
        {
            await Task.Delay(_taskLookupDelay, cancellationToken).ConfigureAwait(false);
        }

        ApplySchedule();

        // An interval trigger waits an hour if its task never ran, whatever the interval, so the first run is started here.
        if (WorkerOf<IncrementalSyncTask>() is { LastExecutionResult: null })
        {
            _taskManager.QueueScheduledTask<IncrementalSyncTask>(new TaskOptions());
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySchedule();

    private IScheduledTaskWorker? WorkerOf<T>()
        where T : IScheduledTask
    {
        return _taskManager.ScheduledTasks.FirstOrDefault(worker => worker.ScheduledTask is T);
    }

    private void Apply<T>(IReadOnlyList<TaskTriggerInfo> triggers)
        where T : IScheduledTask
    {
        if (WorkerOf<T>() is not { } worker || AreSame(worker.Triggers, triggers))
        {
            return;
        }

        try
        {
            worker.Triggers = triggers;
            _logger.LogInformation("Schedule of {Task} updated from the configuration", worker.Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot save the schedule of {Task}: {Error}", worker.Name, exception.GetType().Name);
        }
    }
}
