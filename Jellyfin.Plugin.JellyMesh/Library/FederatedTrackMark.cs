using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Tells a track stub of another JellyMesh, so that a remote read never imports it (row 455). 12.4 gives an audio
/// <c>.strm</c> its file path, not its URL, so the folder shape is what tells it.
/// </summary>
public static class FederatedTrackMark
{
    /// <summary>Tells whether a remote track is a stub of another JellyMesh, by the path of the item or of a source.</summary>
    /// <param name="path">The path of the track on the remote.</param>
    /// <param name="sources">The media sources the remote sent.</param>
    /// <returns>Whether it is a stub.</returns>
    public static bool IsStub(string? path, IEnumerable<RemoteMediaSource>? sources)
        => HasStubShape(path) || (sources?.Any(source => HasStubShape(source.Path)) ?? false);

    // Looks for {server guid}/{library id}/Musica anywhere in the path, since the root of the remote is not known.
    private static bool HasStubShape(string? path)
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
                && string.Equals(parts[i + 2], TypeFolder.Music.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
