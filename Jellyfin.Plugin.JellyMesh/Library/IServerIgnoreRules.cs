namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The rules by which the server skips files and folders in a library scan.
/// </summary>
public interface IServerIgnoreRules
{
    /// <summary>Tells whether the server would skip an entry because of its own name, and not because of its folder.</summary>
    /// <param name="folder">The folder of the entry, relative to the federated folder.</param>
    /// <param name="name">The name of the entry.</param>
    /// <param name="isDirectory">Whether the entry is a folder.</param>
    /// <returns>Whether the server would skip it.</returns>
    bool Ignores(string folder, string name, bool isDirectory);
}
