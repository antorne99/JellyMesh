using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The local film that heads the versions of a group: its first local copy found, or the film the administrator merged
/// that copy into as a version (§10 rows 148, 162).
/// </summary>
internal static class LocalVersionHead
{
    /// <summary>
    /// Finds the local film that heads the versions of a group.
    /// </summary>
    /// <param name="localItemIds">The local copies of the group.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="isStub">Whether an item is a stub of the federated folder.</param>
    /// <returns>The film, null when no local copy is found.</returns>
    public static Video? Of(IEnumerable<Guid> localItemIds, ILibraryManager library, Func<BaseItem, bool> isStub)
    {
        var local = localItemIds.Select(id => FindLocal(id, library)).OfType<Video>().FirstOrDefault();
        var seen = new HashSet<Guid>();
        while (local is { PrimaryVersionId: { } above } && seen.Add(local.Id)
            && library.GetItemById(above) is Video { OwnerId: var owner } film && owner.Equals(Guid.Empty) && !isStub(film))
        {
            local = film;
        }

        return local;
    }

    // A file-based alternate of a local film stands for its owner, which Jellyfin shows.
    private static Video? FindLocal(Guid id, ILibraryManager library)
    {
        if (library.GetItemById(id) is not Video video)
        {
            return null;
        }

        return !video.OwnerId.Equals(Guid.Empty) && library.GetItemById(video.OwnerId) is Video owner ? owner : video;
    }
}
