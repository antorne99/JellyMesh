using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The shared libraries, one per type with the type folders of every server in any library mode (§10 row 442).
/// </summary>
internal static class SharedLibraries
{
    /// <summary>
    /// The owner of the shared libraries where the libraries of a server have their server: no server has this id.
    /// </summary>
    public static readonly Guid Owner = Guid.Empty;

    private const string BaseName = "Tutti i server";

    /// <summary>
    /// Lists the names the shared library of a kind may take: the second has the first digits of the local server id (D41).
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="systemId">The id of the local server.</param>
    /// <returns>The names, before the server makes them valid file names.</returns>
    public static IReadOnlyList<string> NamesFor(ServerLibraryKind kind, string systemId)
    {
        var type = LibraryPlan.TypeOf(kind).Name;
        var digits = new string((systemId ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Take(8).ToArray());
        return digits.Length == 8 ? [$"{BaseName} - {type}", $"{BaseName} {digits} - {type}"] : [$"{BaseName} - {type}"];
    }

    /// <summary>
    /// Plans the shared library of a kind from the folders of each server, without repeats.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="folders">The folders of each server: those of its imported libraries and those holding titles.</param>
    /// <returns>The library.</returns>
    public static PlannedLibrary Plan(ServerLibraryKind kind, IEnumerable<IEnumerable<string>> folders)
    {
        var paths = new List<string>();
        foreach (var path in folders.SelectMany(server => server))
        {
            if (!paths.Any(other => FederatedFolderLinkChecker.IsSamePath(other, path)))
            {
                paths.Add(path);
            }
        }

        return new PlannedLibrary(kind.ToString(), kind, string.Empty, LibraryPlan.TypeOf(kind).Name, paths);
    }

    /// <summary>
    /// Lists the folders of the imported libraries of a server of a kind, as a by-type library takes them.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="root">The federated folder.</param>
    /// <param name="olderLayout">Whether the titles are still in the 0.5 layout.</param>
    /// <returns>The folders.</returns>
    public static IReadOnlyList<string> FoldersOf(RemoteServerConfig server, ServerLibraryKind kind, string root, bool olderLayout)
        => [.. LibraryPlan.For(server, root, olderLayout, LibraryMode.ByType).Where(planned => planned.Kind == kind).SelectMany(planned => planned.Paths)];
}
