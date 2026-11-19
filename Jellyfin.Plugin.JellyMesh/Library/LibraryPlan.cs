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
    /// <param name="mode">The mode to plan for, the saved one when null.</param>
    /// <returns>The libraries, none in "none" mode.</returns>
    public static IReadOnlyList<PlannedLibrary> For(RemoteServerConfig server, string root, bool olderLayout, LibraryMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var imported = Imported(server);
        switch (mode ?? server.LibraryMode)
        {
            case LibraryMode.ByType:
                return [.. KindsOf(imported, olderLayout).Select(kind => new PlannedLibrary(
                    kind.ToString(),
                    kind,
                    string.Empty,
                    TypeOf(kind).Name,
                    olderLayout
                        ? [ServerFolderOf(kind, root, server.Id)!]
                        : [.. imported.SelectMany(library => library.PathsOf(kind, root, server.Id))]))];
            case LibraryMode.ByRemoteLibrary when !olderLayout:
                return [.. imported.Select(library => new PlannedLibrary(library.Id, library.Kind, library.Id, library.Name, library.Paths(root, server.Id)))];
            default:
                return [];
        }
    }

    /// <summary>
    /// Lists the type folders of the imported libraries of a server, which the admin adds to libraries by hand in "none" mode.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="root">The federated folder.</param>
    /// <param name="olderLayout">Whether its titles are still in the 0.5 layout, where only the folders of the server exist.</param>
    /// <returns>Each folder with the local library type it needs.</returns>
    public static IReadOnlyList<ManualLibraryPath> ManualPaths(RemoteServerConfig server, string root, bool olderLayout)
    {
        ArgumentNullException.ThrowIfNull(server);
        var imported = Imported(server);
        return olderLayout
            ? [.. KindsOf(imported, true).Select(kind => new ManualLibraryPath(ServerFolderOf(kind, root, server.Id)!, TypeOf(kind).LibraryType.ToString(), string.Empty))]
            : [.. imported.Where(library => library.Kind != ServerLibraryKind.Mixed)
                .Select(library => new ManualLibraryPath(library.Paths(root, server.Id)[0], TypeOf(library.Kind).LibraryType.ToString(), library.Name))];
    }

    /// <summary>
    /// Lists the type folders of the imported libraries of a server, relative to the federated folder.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <returns>The folders.</returns>
    public static IReadOnlyList<string> TypeFolders(RemoteServerConfig server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return [.. Imported(server).SelectMany(library => ServerLibraryKinds.TypeFoldersOf(library.Kind).Select(type => FederatedPaths.TypeFolderOf(server.Id, library.Id, type)))];
    }

    /// <summary>
    /// Gets the 0.5 folder of a server for a kind, which held its titles before the remote library layout.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="root">The federated folder.</param>
    /// <param name="serverId">The server.</param>
    /// <returns>The absolute folder, null for a kind the 0.5 layout has no folder for.</returns>
    public static string? ServerFolderOf(ServerLibraryKind kind, string root, Guid serverId) => kind switch
    {
        ServerLibraryKind.Movies => FederatedFolderLayout.ServerMoviesPath(root, serverId),
        ServerLibraryKind.Series => FederatedFolderLayout.ServerSeriesPath(root, serverId),
        _ => null
    };

    /// <summary>
    /// Gets the type folder of a kind of plugin library with one.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The type folder.</returns>
    public static TypeFolder TypeOf(ServerLibraryKind kind) => ServerLibraryKinds.TypeFolderOf(kind);

    // The imported libraries the plugin read, in the order of the remote, with the kind of their type and an id safe as a
    // folder name; others wait for a sync.
    private static List<ImportedLibrary> Imported(RemoteServerConfig server)
        => [.. server.RemoteLibraries
            .Where(library => FederatedPaths.IsLibraryId(library.Id) && server.ImportedLibraryIds.Contains(library.Id, StringComparer.OrdinalIgnoreCase))
            .DistinctBy(library => library.Id, StringComparer.OrdinalIgnoreCase)
            .Select(library => ServerLibraryKinds.Of(library.CollectionType) is { } kind ? new ImportedLibrary(library.Id, library.Name, kind) : null)
            .OfType<ImportedLibrary>()];

    // The by-type kinds with a type folder in an imported library; in the 0.5 layout only those with a folder of the server.
    private static IEnumerable<ServerLibraryKind> KindsOf(IReadOnlyCollection<ImportedLibrary> imported, bool olderLayout)
        => (olderLayout ? ServerLibraryKinds.OfOlderLayout : ServerLibraryKinds.OfOneType)
            .Where(kind => imported.Any(library => library.Holds(kind)));

    private sealed record ImportedLibrary(string Id, string Name, ServerLibraryKind Kind)
    {
        // Whether it has the type folder of a by-type kind: a mixed library has those of films and series [D14].
        public bool Holds(ServerLibraryKind kind) => ServerLibraryKinds.TypeFoldersOf(Kind).Contains(TypeOf(kind));

        public IReadOnlyList<string> Paths(string root, Guid serverId)
            => [.. ServerLibraryKinds.TypeFoldersOf(Kind).Select(type => FederatedFolderLayout.TypeFolderPath(root, serverId, Id, type))];

        public IEnumerable<string> PathsOf(ServerLibraryKind kind, string root, Guid serverId)
            => Holds(kind) ? [FederatedFolderLayout.TypeFolderPath(root, serverId, Id, TypeOf(kind))] : [];
    }
}
