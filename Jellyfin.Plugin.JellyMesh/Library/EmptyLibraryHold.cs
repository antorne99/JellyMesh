using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The empty library hold of decision 44, shared by the stub planner and the layout migration so that the two agree.
/// </summary>
internal static class EmptyLibraryHold
{
    /// <summary>
    /// Finds the imported libraries of configured servers that a full read shows empty, among the sources of one stub.
    /// Sources on servers missing from the configuration, or with no library, hold nothing.
    /// </summary>
    /// <param name="snapshot">The index.</param>
    /// <param name="servers">The configured servers.</param>
    /// <param name="sources">The sources of the stub.</param>
    /// <returns>The distinct empty libraries, none if the stub is not held; null when the index proves nothing, being
    /// damaged or not reading a configured server in full.</returns>
    public static IReadOnlyList<(Guid ServerId, string LibraryId)>? Of(
        IndexSnapshot snapshot,
        IReadOnlyDictionary<Guid, RemoteServerInfo> servers,
        IEnumerable<StubSource> sources)
    {
        if (snapshot.Health != IndexHealth.Ok)
        {
            return null;
        }

        var empty = new List<(Guid ServerId, string LibraryId)>();
        foreach (var source in sources)
        {
            if (!servers.TryGetValue(source.ServerId, out var server))
            {
                continue;
            }

            if (!snapshot.IsAuthoritativeFor(source.ServerId))
            {
                return null;
            }

            var library = source.LibraryId;
            if (library is null || !server.ImportedLibraryIds.Contains(library))
            {
                continue;
            }

            if (!snapshot.Find(source.ServerId)!.Items.Any(item => string.Equals(item.LibraryId, library, StringComparison.Ordinal)))
            {
                empty.Add((source.ServerId, library));
            }
        }

        return empty.Distinct().ToList();
    }
}
