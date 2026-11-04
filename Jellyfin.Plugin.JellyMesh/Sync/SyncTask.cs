using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// What the two scheduled syncs share. Jellyfin finds the concrete classes by itself and builds them through DI.
/// </summary>
public abstract class SyncTask : IScheduledTask
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncTask"/> class.
    /// </summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="logger">The logger.</param>
    protected SyncTask(IPluginConfigurationStore store, ILogger logger)
    {
        Store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Key { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public string Category => "JellyMesh";

    /// <summary>
    /// Gets the configuration store.
    /// </summary>
    protected IPluginConfigurationStore Store { get; }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        SyncStartResult result;
        try
        {
            result = await RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Jellyfin stores and logs what a task throws, so it gets the type only: a message could hold a remote address.
            _logger.LogError("{Task} stopped: {Error}", Name, exception.GetType().Name);
            throw new InvalidOperationException($"{Name} stopped: {exception.GetType().Name}");
        }

        if (result == SyncStartResult.Suspended)
        {
            _logger.LogWarning("{Task} skipped: remote servers are suspended until the secret key is fixed", Name);
        }
        else if (result == SyncStartResult.AlreadyRunning)
        {
            _logger.LogInformation("{Task} skipped: a sync is already running", Name);
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public abstract IEnumerable<TaskTriggerInfo> GetDefaultTriggers();

    /// <summary>
    /// Runs the sync.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it ran.</returns>
    protected abstract Task<SyncStartResult> RunAsync(CancellationToken cancellationToken);
}
