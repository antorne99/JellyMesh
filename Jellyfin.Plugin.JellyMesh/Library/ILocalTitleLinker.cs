using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Links one local title to its stubs as soon as Jellyfin has read its metadata, without waiting for a sync (Q245).
/// </summary>
public interface ILocalTitleLinker
{
    /// <summary>
    /// Matches the catalog again and links the stubs of this one title to its local copy. Writes no file and calls no
    /// remote: what else changed waits for the next sync.
    /// </summary>
    /// <param name="itemId">The local film or series.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task LinkAsync(Guid itemId, CancellationToken cancellationToken);
}
