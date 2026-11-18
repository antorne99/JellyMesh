using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Reads every remote in full at the configured time, which is what catches deletions (plan §3.1), then copies the plugin
/// database (row 427), empties its log and deletes the damaged files past their 30 days (D35).
/// </summary>
public sealed class ReconciliationTask : SyncTask
{
    private readonly ICatalogSyncService _sync;
    private readonly IPluginDatabase _database;
    private readonly ILogger<ReconciliationTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationTask"/> class.
    /// </summary>
    /// <param name="sync">The sync service.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="database">The plugin database, copied after each reconciliation.</param>
    /// <param name="logger">The logger.</param>
    public ReconciliationTask(ICatalogSyncService sync, IPluginConfigurationStore store, IPluginDatabase database, ILogger<ReconciliationTask> logger)
        : base(store, logger)
    {
        _sync = sync;
        _database = database;
        _logger = logger;
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
    /// <remarks>Jellyfin asks while it starts, so the defaults stand in while the settings cannot be read.</remarks>
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => Store.ReadOr(SyncSchedule.ReconciliationTriggers, SyncSchedule.ReconciliationTriggers(new PluginConfiguration()));

    /// <inheritdoc />
    protected override async Task<SyncStartResult> RunAsync(CancellationToken cancellationToken)
    {
        // The copy follows the sync, so it holds what the sync brought in, and is made even when the sync failed.
        try
        {
            return await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await BackUpAsync(cancellationToken).ConfigureAwait(false);
                await EmptyLogAsync(cancellationToken).ConfigureAwait(false);
                await DeleteExpiredDamagedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // A copy that cannot be made is logged and leaves the copies before it; it does not fail the reconciliation.
    private async Task BackUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.BackUpAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (StoreSuspendedException)
        {
            _logger.LogWarning("The nightly copy of the JellyMesh database was skipped: the database is suspended");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("The nightly copy of the JellyMesh database failed: {Error}", exception.Message);
        }
    }

    // Empties the database log at least once a night, so no replaced credential outlives a busy log; never fails the reconciliation.
    private async Task EmptyLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await _database.TruncateWalAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("The JellyMesh database log was busy after the nightly copy; it is emptied at the next chance");
            }
        }
        catch (StoreSuspendedException)
        {
            _logger.LogWarning("The JellyMesh database log was not emptied: the database is suspended");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("The JellyMesh database log could not be emptied: {Error}", exception.Message);
        }
    }

    // Damaged files that cannot go yet are logged and stay for the next night; it does not fail the reconciliation (D35).
    private async Task DeleteExpiredDamagedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.DeleteExpiredDamagedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (StoreSuspendedException)
        {
            _logger.LogWarning("The expired damaged JellyMesh database files were kept: the database is suspended");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("The expired damaged JellyMesh database files cannot be deleted: {Error}", exception.Message);
        }
    }

    private async Task<SyncStartResult> ReconcileAsync(CancellationToken cancellationToken)
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
