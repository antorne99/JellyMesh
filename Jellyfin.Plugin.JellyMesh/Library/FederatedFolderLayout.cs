using System.IO;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The subfolders of the federated folder, which are what gets linked to the libraries.
/// </summary>
public static class FederatedFolderLayout
{
    /// <summary>
    /// Gets the subfolder of the films.
    /// </summary>
    /// <param name="federatedFolderPath">The federated folder.</param>
    /// <returns>The path.</returns>
    public static string MoviesPath(string federatedFolderPath) => Path.Combine(federatedFolderPath, "Film");

    /// <summary>
    /// Gets the subfolder of the series.
    /// </summary>
    /// <param name="federatedFolderPath">The federated folder.</param>
    /// <returns>The path.</returns>
    public static string SeriesPath(string federatedFolderPath) => Path.Combine(federatedFolderPath, "Serie");
}
