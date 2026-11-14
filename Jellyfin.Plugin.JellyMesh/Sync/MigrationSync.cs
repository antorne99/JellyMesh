using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// <see cref="IMigrationSync"/> through the incremental sync. The sync service is found at the first call, since it
/// depends on the migration itself.
/// </summary>
public sealed class MigrationSync : IMigrationSync
{
    private readonly Func<ICatalogSyncService> _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="MigrationSync"/> class.
    /// </summary>
    /// <param name="services">The services, which hold the sync service.</param>
    public MigrationSync(IServiceProvider services)
        : this(services.GetRequiredService<ICatalogSyncService>)
    {
    }

    internal MigrationSync(Func<ICatalogSyncService> sync)
    {
        _sync = sync;
    }

    /// <inheritdoc />
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        var sync = _sync();
        if (await sync.RunIncrementalAsync(cancellationToken).ConfigureAwait(false) == SyncStartResult.AlreadyRunning)
        {
            await sync.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
