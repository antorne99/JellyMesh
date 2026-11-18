using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Tells what to do with a library the plugin wants, from the libraries of the admin on its paths (§10 rows 440-441).
/// </summary>
internal static class LibraryAdoption
{
    /// <summary>
    /// Classifies the libraries of the admin against a library the plugin wants.
    /// </summary>
    /// <param name="planned">The library the plugin wants, with all its paths.</param>
    /// <param name="type">The local type it would have.</param>
    /// <param name="folders">The libraries of the server no registry holds.</param>
    /// <param name="root">The federated folder.</param>
    /// <param name="serverId">The server the library is for.</param>
    /// <returns>The library to adopt, whether to wait, and what is in the way.</returns>
    public static AdoptionOutcome Classify(PlannedLibrary planned, CollectionTypeOptions type, IEnumerable<VirtualFolderInfo> folders, string root, Guid serverId)
    {
        var exact = new List<VirtualFolderInfo>();
        var conflicts = new List<LibraryConflict>();
        foreach (var folder in folders)
        {
            if (ReasonOf(planned, type, folder, root, serverId) is not { } reason)
            {
                continue;
            }

            if (reason == LibraryConflictReason.SamePaths)
            {
                exact.Add(folder);
            }
            else
            {
                conflicts.Add(ConflictOf(planned, folder, reason));
            }
        }

        if (exact.Count > 1)
        {
            conflicts.AddRange(exact.Select(folder => ConflictOf(planned, folder, LibraryConflictReason.SamePaths)));
        }

        if (conflicts.Count > 0 || exact.Count == 0)
        {
            return new AdoptionOutcome(null, false, conflicts);
        }

        // A library with no id yet may be one just made or renamed: it is taken at a later round.
        return string.IsNullOrEmpty(exact[0].ItemId) ? new AdoptionOutcome(null, true, []) : new AdoptionOutcome(exact[0], false, []);
    }

    // Null when the library does not touch the paths or also holds folders of other servers, as the libraries of row 77 do;
    // SamePaths when it holds exactly them with the type. A location that is no path holds no folder.
    private static LibraryConflictReason? ReasonOf(PlannedLibrary planned, CollectionTypeOptions type, VirtualFolderInfo folder, string root, Guid serverId)
    {
        var locations = (folder.Locations ?? []).Where(FederatedFolderLinkChecker.IsPath).ToArray();
        if (locations.Any(location => planned.Paths.Any(path => IsStrictAncestor(location, path))))
        {
            return LibraryConflictReason.ParentPath;
        }

        if (!locations.Any(location => planned.Paths.Any(path => FederatedFolderLinkChecker.IsAncestorOrSame(path, location)))
            || locations.Any(location => IsOtherServerFolder(location, root, serverId)))
        {
            return null;
        }

        if (folder.CollectionType != type)
        {
            return LibraryConflictReason.OtherType;
        }

        var same = locations.Length == planned.Paths.Count
            && planned.Paths.All(path => locations.Any(location => FederatedFolderLinkChecker.IsSamePath(location, path)))
            && locations.All(location => planned.Paths.Any(path => FederatedFolderLinkChecker.IsSamePath(location, path)));
        return same ? LibraryConflictReason.SamePaths : LibraryConflictReason.OtherPaths;
    }

    // A folder under the federated folder of a server other than the given one.
    private static bool IsOtherServerFolder(string location, string root, Guid serverId) =>
        FederatedPaths.RelativeTo(root, Path.TrimEndingDirectorySeparator(location))?.Split(Path.DirectorySeparatorChar) is [var server, ..]
        && Guid.TryParseExact(server, "D", out var id)
        && id != serverId;

    private static bool IsStrictAncestor(string location, string path) =>
        FederatedFolderLinkChecker.IsAncestorOrSame(location, path) && !FederatedFolderLinkChecker.IsSamePath(location, path);

    private static LibraryConflict ConflictOf(PlannedLibrary planned, VirtualFolderInfo folder, LibraryConflictReason reason) =>
        new(folder.Name ?? string.Empty, reason, planned.Kind, planned.RemoteLibraryId, planned.Paths);
}
