using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// The last syncs that ran (decision 48), newest first.
/// </summary>
public interface ISyncHistory
{
    /// <summary>
    /// Gets the runs, newest first. The copy never changes.
    /// </summary>
    IReadOnlyList<SyncRun> Runs { get; }

    /// <summary>
    /// Gets a value indicating whether the file could not be read, so the runs before it were set aside.
    /// </summary>
    bool IsDamaged { get; }

    /// <summary>
    /// Adds a run, dropping the oldest past the limit. The run shows at once, then the history is saved.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="System.IO.IOException">The history could not be saved. The run still shows.</exception>
    Task AddAsync(SyncRun run, CancellationToken cancellationToken);
}
