using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Reads every remote in full at the configured time, which is what catches deletions (plan §3.1).
/// </summary>
public sealed class ReconciliationTask : SyncTask
{
    private readonly ICatalogSyncService _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationTask"/> class.
    /// </summary>
    /// <param name="sync">The sync service.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    public ReconciliationTask(ICatalogSyncService sync, IPluginConfigurationStore store, ILogger<ReconciliationTask> logger)
        : base(store, logger)
    {
        _sync = sync;
    }

    /// <inheritdoc />
    public override string Name => "JellyMesh reconciliation";

    /// <inheritdoc />
    public override string Key => "JellyMeshReconciliation";

    /// <inheritdoc />
    public override string Description => "Reads the remote servers in full, to catch what was removed. The time is set on the JellyMesh configuration page.";

    /// <summary>
    /// Gets how long to wait for a running sync before trying again.
    /// </summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Store.Read(SyncSchedule.ReconciliationTriggers);

    /// <inheritdoc />
    protected override async Task<SyncStartResult> RunAsync(CancellationToken cancellationToken)
    {
        // It runs once a day, so unlike a poll it cannot leave it to the next turn when another sync is in the way.
        var result = await _sync.RunAsync(cancellationToken).ConfigureAwait(false);
        while (result == SyncStartResult.AlreadyRunning)
        {
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            result = await _sync.RunAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
