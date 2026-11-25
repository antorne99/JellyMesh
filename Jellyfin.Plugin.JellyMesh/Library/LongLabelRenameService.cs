using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// At the start of the server, renames the stubs of each server whose saved label is over the byte limit with the steps of
/// a rename, so that a label saved before the limit was in bytes is cut once (row 564). It waits for a start with no title
/// left in the older layouts, so that the migration finds the item of every old stub (row 565).
/// </summary>
public sealed class LongLabelRenameService : BackgroundService
{
    private readonly IServerRenamer _renamer;
    private readonly IStubRegistry _stubs;
    private readonly ILogger<LongLabelRenameService> _logger;
    private readonly ErrorLogLimits _errorLogs;

    /// <summary>
    /// Initializes a new instance of the <see cref="LongLabelRenameService"/> class.
    /// </summary>
    /// <param name="renamer">The server renamer.</param>
    /// <param name="stubs">The stub registry, which holds the saved labels.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public LongLabelRenameService(IServerRenamer renamer, IStubRegistry stubs, ILogger<LongLabelRenameService> logger, ErrorLogLimits? errorLogs = null)
    {
        _errorLogs = errorLogs ?? new ErrorLogLimits();
        _renamer = renamer;
        _stubs = stubs;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets the pause before reading the settings and the registry again at start.
    /// </summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!await TryRenameAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // What escapes a hosted service stops the whole server; the next start tries again.
            _logger.LogError("Cannot rename the servers with a label over the limit: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
        }
    }

    // Renames the servers with a long label, false while the settings or the registry cannot be read yet. While the older
    // layouts hold titles it renames nothing and saves nothing, so the next start tries again (row 565).
    private async Task<bool> TryRenameAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_stubs.IsUnread)
            {
                await _stubs.ReloadAsync(cancellationToken).ConfigureAwait(false);
                if (_stubs.IsUnread)
                {
                    return false;
                }
            }

            if (_stubs.UsesOlderLayout() is not { } olderLayout)
            {
                return false;
            }

            if (olderLayout)
            {
                _logger.LogDebug("The servers with a label over the limit are renamed at a start after the migration to the server folders");
                return true;
            }

            var ran = await _renamer.RenameLongLabelsAsync(cancellationToken).ConfigureAwait(false);
            if (ran > 0)
            {
                _logger.LogInformation("Renamed the stubs of {Count} servers whose label was over the limit", ran);
            }

            return true;
        }
        catch (StoreSuspendedException)
        {
            return false;
        }
    }
}
