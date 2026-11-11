using System;
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

    /// <summary>
    /// Gets the subfolder of the films of a server.
    /// </summary>
    /// <param name="federatedFolderPath">The federated folder.</param>
    /// <param name="serverId">The server.</param>
    /// <returns>The path.</returns>
    public static string ServerMoviesPath(string federatedFolderPath, Guid serverId) => MoviesPath(ServerPath(federatedFolderPath, serverId));

    /// <summary>
    /// Gets the subfolder of the series of a server.
    /// </summary>
    /// <param name="federatedFolderPath">The federated folder.</param>
    /// <param name="serverId">The server.</param>
    /// <returns>The path.</returns>
    public static string ServerSeriesPath(string federatedFolderPath, Guid serverId) => SeriesPath(ServerPath(federatedFolderPath, serverId));

    /// <summary>
    /// Gets the folder of a server, which holds only the titles and versions of that server.
    /// </summary>
    /// <param name="federatedFolderPath">The federated folder.</param>
    /// <param name="serverId">The server.</param>
    /// <returns>The path.</returns>
    public static string ServerPath(string federatedFolderPath, Guid serverId) => Path.Combine(federatedFolderPath, FederatedPaths.ServerFolder(serverId));
}
