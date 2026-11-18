using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Polls the remotes for what changed, at the configured interval (plan §3.1).
/// </summary>
public sealed class IncrementalSyncTask : SyncTask
{
    private readonly ICatalogSyncService _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrementalSyncTask"/> class.
    /// </summary>
    /// <param name="sync">The sync service.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    public IncrementalSyncTask(ICatalogSyncService sync, IPluginConfigurationStore store, ILogger<IncrementalSyncTask> logger)
        : base(store, logger)
    {
        _sync = sync;
    }

    /// <inheritdoc />
    public override string Name => "JellyMesh incremental sync";

    /// <inheritdoc />
    public override string Key => "JellyMeshIncrementalSync";

    /// <inheritdoc />
    public override string Description => "Reads what changed on the remote servers. The interval is set on the JellyMesh configuration page.";

    /// <inheritdoc />
    /// <remarks>Jellyfin asks while it starts, so the defaults stand in while the settings cannot be read.</remarks>
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => Store.ReadOr(SyncSchedule.IncrementalTriggers, SyncSchedule.IncrementalTriggers(new PluginConfiguration()));

    /// <inheritdoc />
    protected override Task<SyncStartResult> RunAsync(CancellationToken cancellationToken) => _sync.RunIncrementalAsync(cancellationToken);
}
