using System;
using System.Globalization;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// The Jellyfin triggers of the configured schedule. A reconciliation time that cannot be turned into a trigger gives no
/// trigger: the save endpoint refuses such a value, so only an edited file has it. An interval below the minimum runs at
/// the minimum: the sync keeps working, and a remote is never polled faster than that.
/// </summary>
public static class SyncSchedule
{
    /// <summary>
    /// Gets the triggers of the incremental sync.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The triggers.</returns>
    public static TaskTriggerInfo[] IncrementalTriggers(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var seconds = Math.Max(configuration.SyncIntervalSeconds, PluginConfiguration.MinSyncIntervalSeconds);
        return [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromSeconds(seconds).Ticks }];
    }

    /// <summary>
    /// Gets the triggers of the reconciliation.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The triggers.</returns>
    public static TaskTriggerInfo[] ReconciliationTriggers(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return TimeOnly.TryParseExact(configuration.ReconciliationTime, PluginConfiguration.ReconciliationTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = time.Ticks }]
            : [];
    }
}
