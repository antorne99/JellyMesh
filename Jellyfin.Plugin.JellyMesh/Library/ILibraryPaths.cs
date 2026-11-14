namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Adds and removes the folders of the federated layout in the admin's libraries, the only change §10 row 77 allows
/// there: the flat <c>Film</c> and <c>Serie</c> and the server folders. The caller queues the scan afterwards.
/// </summary>
/// <remarks>
/// Called in process, Jellyfin's calls neither queue a scan nor fail on a double addition or a removal of a path not
/// stored so, so each call checks the libraries before and after it (§10 row 85).
/// </remarks>
public interface ILibraryPaths
{
    /// <summary>
    /// Adds a folder to a library, unless the library already has it.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="library">The library name.</param>
    /// <param name="path">The folder, which must exist.</param>
    /// <returns>What was done.</returns>
    LibraryPathOutcome Add(string root, string library, string path);

    /// <summary>
    /// Removes a folder from a library, passing it exactly as the library stores it, unless the library does not have it.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="library">The library name.</param>
    /// <param name="path">The folder.</param>
    /// <returns>What was done.</returns>
    LibraryPathOutcome Remove(string root, string library, string path);
}
