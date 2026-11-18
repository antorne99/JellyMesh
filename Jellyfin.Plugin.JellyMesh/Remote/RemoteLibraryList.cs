using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The importable libraries of an answer, with the number of views it held of any type.
/// </summary>
public sealed class RemoteLibraryList : ReadOnlyCollection<RemoteLibrary>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteLibraryList"/> class.
    /// </summary>
    /// <param name="libraries">The importable libraries.</param>
    /// <param name="viewCount">The views in the answer, of any type.</param>
    public RemoteLibraryList(IList<RemoteLibrary> libraries, int viewCount)
        : base(libraries)
    {
        ViewCount = viewCount;
    }

    /// <summary>
    /// Gets the number of views in the answer, of any type.
    /// </summary>
    public int ViewCount { get; }

    /// <summary>
    /// Gets the views an answer held: the count of a <see cref="RemoteLibraryList"/>, otherwise the libraries themselves.
    /// </summary>
    /// <param name="libraries">The libraries of an answer.</param>
    /// <returns>The number of views.</returns>
    public static int ViewCountOf(IReadOnlyCollection<RemoteLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        return libraries is RemoteLibraryList list ? list.ViewCount : libraries.Count;
    }
}
