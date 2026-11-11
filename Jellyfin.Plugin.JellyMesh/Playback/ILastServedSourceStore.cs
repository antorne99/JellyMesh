using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The last remote server that served each stub to each local user (D15, decision 79). It outlives restarts, so the
/// migration to the folders per server can carry the resume position to that server's version (Q47).
/// </summary>
public interface ILastServedSourceStore
{
    /// <summary>
    /// Finds the server that last served a stub to a user.
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="stubId">The stub.</param>
    /// <returns>The server, null if none is known.</returns>
    Guid? Find(Guid userId, string stubId);

    /// <summary>
    /// Records the server that just served a stub to a user. A failed save is logged, not thrown: playback goes on.
    /// </summary>
    /// <param name="userId">The local user.</param>
    /// <param name="stubId">The stub.</param>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RecordAsync(Guid userId, string stubId, Guid serverId, CancellationToken cancellationToken);
}
