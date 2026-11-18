using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The plugin libraries a library mode wants for a server, from its imported remote libraries as last read (§10 row 437).
/// </summary>
internal static class LibraryPlan
{
    /// <summary>
    /// Lists the libraries the mode of a server wants, in the order to create them.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="root">The federated folder.</param>
    /// <param name="olderLayout">Whether its titles are still in the 0.5 layout, where only the folders of the server exist.</param>
    /// <returns>The libraries, none in "none" mode.</returns>
    public static IReadOnlyList<PlannedLibrary> For(RemoteServerConfig server, string root, bool olderLayout)
    {
        ArgumentNullException.ThrowIfNull(server);
        var imported = Imported(server);
        switch (server.LibraryMode)
        {
            case LibraryMode.ByType:
                return [.. KindsOf(imported).Select(kind => new PlannedLibrary(
                    kind.ToString(),
                    kind,
                    string.Empty,
                    TypeOf(kind).Name,
                    olderLayout
                        ? [kind == ServerLibraryKind.Series ? FederatedFolderLayout.ServerSeriesPath(root, server.Id) : FederatedFolderLayout.ServerMoviesPath(root, server.Id)]
                        : [.. imported.Where(library => library.Kind == kind).Select(library => library.Path(root, server.Id))]))];
            case LibraryMode.ByRemoteLibrary when !olderLayout:
                return [.. imported.Select(library => new PlannedLibrary(library.Id, library.Kind, library.Id, library.Name, [library.Path(root, server.Id)]))];
            default:
                return [];
        }
    }

    /// <summary>
    /// Lists the type folders of the imported libraries of a server, which the admin adds to libraries by hand in "none" mode.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="root">The federated folder.</param>
    /// <returns>Each folder with the local library type it needs.</returns>
    public static IReadOnlyList<ManualLibraryPath> ManualPaths(RemoteServerConfig server, string root)
    {
        ArgumentNullException.ThrowIfNull(server);
        return [.. Imported(server).Select(library => new ManualLibraryPath(library.Path(root, server.Id), TypeOf(library.Kind).LibraryType.ToString(), library.Name))];
    }

    /// <summary>
    /// Gets the type folder of a kind of plugin library.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The type folder.</returns>
    public static TypeFolder TypeOf(ServerLibraryKind kind) => kind == ServerLibraryKind.Series ? TypeFolder.Series : TypeFolder.Movies;

    // The imported libraries the plugin read, in the order of the remote, with the kind of their type; others wait for a sync.
    private static List<ImportedLibrary> Imported(RemoteServerConfig server)
        => [.. server.RemoteLibraries
            .Where(library => server.ImportedLibraryIds.Contains(library.Id, StringComparer.OrdinalIgnoreCase))
            .Select(library => KindOf(library.CollectionType) is { } kind ? new ImportedLibrary(library.Id, library.Name, kind) : null)
            .OfType<ImportedLibrary>()];

    private static IEnumerable<ServerLibraryKind> KindsOf(IReadOnlyCollection<ImportedLibrary> imported)
        => Enum.GetValues<ServerLibraryKind>().Where(kind => imported.Any(library => library.Kind == kind));

    private static ServerLibraryKind? KindOf(string collectionType) => collectionType.ToLowerInvariant() switch
    {
        "movies" => ServerLibraryKind.Movies,
        "tvshows" => ServerLibraryKind.Series,
        _ => null
    };

    private sealed record ImportedLibrary(string Id, string Name, ServerLibraryKind Kind)
    {
        public string Path(string root, Guid serverId) => FederatedFolderLayout.TypeFolderPath(root, serverId, Id, TypeOf(Kind));
    }
}
