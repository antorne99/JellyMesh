using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Waits for the library scans of the local server, since a scan requested during another is dropped.
/// </summary>
public interface ILibraryScans
{
    /// <summary>
    /// Waits until no library scan is running.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the server is idle.</returns>
    Task WaitIdleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits for the running scan, queues one and waits until it ends.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the queued scan ended.</returns>
    Task QueueAndWaitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Waits until no library scan is running and every library has its id, for at most the limit.
    /// </summary>
    /// <param name="limit">The longest wait.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the libraries are ready.</returns>
    Task<bool> WaitReadyAsync(TimeSpan limit, CancellationToken cancellationToken);
}
