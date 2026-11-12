using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A library in the registry.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The name as last seen.</param>
public sealed record AutomaticLibraryStatus(ServerLibraryKind Kind, string Name);
