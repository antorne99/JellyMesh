using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Watches for the end of the refreshes asked for new local titles, and links each title at once (Q245). Jellyfin saves an
/// item at the end of its refresh and stamps it then, so a save of a stamped item is the refresh arriving.
/// </summary>
public sealed class LocalRefreshWatcher : IHostedService, IDisposable
{
    private readonly ILibraryManager _library;
    private readonly ILocalRefreshRequester _refreshes;
    private readonly ILocalTitleLinker _linker;
    private readonly ILogger<LocalRefreshWatcher> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalRefreshWatcher"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="refreshes">The refreshes asked for.</param>
    /// <param name="linker">What links one title.</param>
    /// <param name="logger">The logger.</param>
    public LocalRefreshWatcher(ILibraryManager library, ILocalRefreshRequester refreshes, ILocalTitleLinker linker, ILogger<LocalRefreshWatcher> logger)
    {
        _library = library;
        _refreshes = refreshes;
        _linker = linker;
        _logger = logger;
    }

    /// <summary>
    /// Gets the last link started, for tests.
    /// </summary>
    internal Task LastLink { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemUpdated -= OnItemUpdated;
        await _shutdown.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _shutdown.Dispose();

    // Runs on the thread that saved the item, so the link goes to the thread pool.
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not { } item || item.DateLastRefreshed == DateTime.MinValue || !_refreshes.TryTakeArrival(item.Id))
        {
            return;
        }

        var cancellationToken = _shutdown.Token;
        LastLink = Task.Run(() => LinkAsync(item.Id, cancellationToken), CancellationToken.None);
    }

    private async Task LinkAsync(Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            await _linker.LinkAsync(itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The server is stopping: the next sync links the title.
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Could not link a new local title yet, the next sync does: {Error}", exception.GetType().Name);
        }
    }
}
