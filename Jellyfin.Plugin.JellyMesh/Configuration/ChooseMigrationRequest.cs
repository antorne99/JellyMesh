using Jellyfin.Plugin.JellyMesh.Library;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The admin's choice in the migration banner (Q55).
/// </summary>
public sealed record ChooseMigrationRequest
{
    /// <summary>Gets the mode chosen.</summary>
    public LayoutMigrationMode Mode { get; init; }
}
