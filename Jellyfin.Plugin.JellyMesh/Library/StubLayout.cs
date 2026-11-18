using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Which layout the stubs follow: the remote library layout, or an older one while any of its stubs is left.
/// </summary>
public static class StubLayout
{
    /// <summary>Tells whether any stub or series is still in the flat folders or in the 0.5 server folders.</summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="series">The series stubs.</param>
    /// <returns>True while the older layout is in use, so that new titles follow it too.</returns>
    public static bool IsLegacy(IEnumerable<StubEntry> stubs, IEnumerable<SeriesStub> series)
    {
        ArgumentNullException.ThrowIfNull(stubs);
        ArgumentNullException.ThrowIfNull(series);
        return stubs.Any(stub => FederatedPaths.RemoteLibraryOf(stub.RelativePath) is null)
            || series.Any(entry => FederatedPaths.RemoteLibraryOf(entry.RelativePath) is null);
    }

    /// <summary>Tells whether the registry is still on an older layout.</summary>
    /// <param name="state">The registry.</param>
    /// <returns>True while the older layout is in use.</returns>
    public static bool IsLegacy(StubState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return IsLegacy(state.Stubs, state.Series);
    }

    /// <summary>Gives the relative type folders of a server that hold its stubs or series of one type.</summary>
    /// <param name="state">The registry.</param>
    /// <param name="serverId">The server.</param>
    /// <param name="type">The type folder.</param>
    /// <returns>The folders, in id order.</returns>
    public static IReadOnlyList<string> TypeFoldersOf(StubState state, Guid serverId, TypeFolder type)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(type);
        return state.Stubs.Where(stub => stub.ServerId == serverId).Select(stub => stub.RelativePath)
            .Concat(state.Series.Where(series => series.ServerId == serverId).Select(series => series.RelativePath))
            .Select(path => FederatedPaths.RemoteLibraryOf(path) is { } library
                && FederatedPaths.TypeFolderOf(serverId, library, type) is var folder
                && path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? folder : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
