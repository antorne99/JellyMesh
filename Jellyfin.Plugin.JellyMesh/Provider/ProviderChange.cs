using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// An item that changed.
/// </summary>
/// <param name="Id">The item.</param>
/// <param name="Type">The item type.</param>
/// <param name="LibraryIds">The shared libraries it is in.</param>
public sealed record ProviderChange(string Id, string Type, IReadOnlyList<string> LibraryIds);
