using System.Collections.Generic;
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
    /// <returns>The library to adopt, whether to wait, and what is in the way.</returns>
    public static AdoptionOutcome Classify(PlannedLibrary planned, CollectionTypeOptions type, IEnumerable<VirtualFolderInfo> folders)
    {
        var exact = new List<VirtualFolderInfo>();
        var conflicts = new List<LibraryConflict>();
        foreach (var folder in folders)
        {
            if (ReasonOf(planned, type, folder) is not { } reason)
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

    // Null when the library does not touch the paths; SamePaths when it holds exactly them with the type.
    private static LibraryConflictReason? ReasonOf(PlannedLibrary planned, CollectionTypeOptions type, VirtualFolderInfo folder)
    {
        var locations = folder.Locations ?? [];
        if (locations.Any(location => planned.Paths.Any(path => IsStrictAncestor(location, path))))
        {
            return LibraryConflictReason.ParentPath;
        }

        if (!locations.Any(location => planned.Paths.Any(path => FederatedFolderLinkChecker.IsAncestorOrSame(path, location))))
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

    private static bool IsStrictAncestor(string location, string path) =>
        FederatedFolderLinkChecker.IsAncestorOrSame(location, path) && !FederatedFolderLinkChecker.IsSamePath(location, path);

    private static LibraryConflict ConflictOf(PlannedLibrary planned, VirtualFolderInfo folder, LibraryConflictReason reason) =>
        new(folder.Name ?? string.Empty, reason, planned.Kind, planned.RemoteLibraryId, planned.Paths);
}
