using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Renames a server with its stubs and automatic libraries (Q38).
/// </summary>
public interface IServerRenamer
{
    /// <summary>
    /// Gets a value indicating whether a library the plugin renamed keeps its old id until a restart (§10 row 86).
    /// </summary>
    bool NeedsRestart { get; }

    /// <summary>
    /// Gets the state of the rename of a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The state.</returns>
    ServerRenameState GetState(Guid serverId);

    /// <summary>
    /// Renames a server, then in the background moves its stubs to the new label, renames its automatic libraries, scans
    /// and links the versions. The same name runs the steps again, which finishes a rename that failed.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="name">The new name.</param>
    /// <param name="confirmed">Whether the admin confirmed after the warning.</param>
    /// <returns>The answer.</returns>
    ServerRenameRequest Rename(Guid serverId, string? name, bool confirmed);
}
