using System;
using System.Globalization;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// The Jellyfin triggers of the configured schedule. A value that cannot be turned into a trigger gives no trigger rather
/// than a remote polled at a rate nobody chose: the save endpoint refuses such values, so only an edited file has them.
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

        return configuration.SyncIntervalSeconds > 0
            ? [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromSeconds(configuration.SyncIntervalSeconds).Ticks }]
            : [];
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
