namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The step of the manual migration the admin is at (Q40, Q48).
/// </summary>
public enum LayoutMigrationStep
{
    /// <summary>The libraries on the old paths lack some new paths: the admin adds them (Q49 holds until then).</summary>
    AddPaths,

    /// <summary>The new paths are linked: the plugin carries the state after their scan and then empties the old folders.</summary>
    Carrying,

    /// <summary>The old folders are empty: the admin removes the old paths from the libraries.</summary>
    RemoveOldPaths
}
