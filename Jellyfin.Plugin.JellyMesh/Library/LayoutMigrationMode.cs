namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// How the admin chose to migrate from the flat layout, in the banner (Q55).
/// </summary>
public enum LayoutMigrationMode
{
    /// <summary>Not chosen yet: the old layout stays as it is (Q49, §10 row 217).</summary>
    None,

    /// <summary>The admin adds and removes the paths by hand (Q40).</summary>
    Manual,

    /// <summary>The plugin adds and removes the paths itself (Q56).</summary>
    Automatic
}
