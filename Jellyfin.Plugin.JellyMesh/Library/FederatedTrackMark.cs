using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Tells a track or audiobook stub of another JellyMesh, so that a remote read never imports it (row 455). 12.4 gives an
/// audio <c>.strm</c> its file path, not its URL, so the folder shape is what tells it.
/// </summary>
public static class FederatedTrackMark
{
    /// <summary>Tells whether a remote item is a stub of another JellyMesh, by the path of the item or of a source.</summary>
    /// <param name="path">The path of the item on the remote.</param>
    /// <param name="sources">The media sources the remote sent.</param>
    /// <param name="typeFolder">The type folder the stubs of its kind are written in.</param>
    /// <returns>Whether it is a stub.</returns>
    public static bool IsStub(string? path, IEnumerable<RemoteMediaSource>? sources, TypeFolder typeFolder)
    {
        ArgumentNullException.ThrowIfNull(typeFolder);
        return HasStubShape(path, typeFolder.Name) || (sources?.Any(source => HasStubShape(source.Path, typeFolder.Name)) ?? false);
    }

    // Looks for {server guid}/{library id}/{type folder} anywhere in the path, since the root of the remote is not known.
    private static bool HasStubShape(string? path, string typeFolder)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 3 < parts.Length; i++)
        {
            if (Guid.TryParseExact(parts[i], "D", out _)
                && FederatedPaths.IsLibraryId(parts[i + 1])
                && string.Equals(parts[i + 2], typeFolder, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
