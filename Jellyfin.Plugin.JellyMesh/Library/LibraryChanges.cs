using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The libraries that came over the federated folder or left it since the last sync.
/// </summary>
/// <param name="Added">The libraries that now hold a path in it, over it or on it.</param>
/// <param name="Removed">The libraries that no longer do.</param>
public sealed record LibraryChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    /// <summary>Gets the answer when nothing changed or nothing could be compared.</summary>
    public static LibraryChanges None { get; } = new([], []);
}
