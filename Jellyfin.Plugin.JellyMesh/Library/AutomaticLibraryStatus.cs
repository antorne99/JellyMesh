using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A library in the registry.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Name">The name as last seen.</param>
public sealed record AutomaticLibraryStatus(ServerLibraryKind Kind, string Name)
{
    /// <summary>Gets a value indicating whether the library was the admin's and is adopted.</summary>
    public bool Adopted { get; init; }

    /// <summary>Gets a value indicating whether the library is switched off in the dashboard.</summary>
    public bool Disabled { get; init; }
}
