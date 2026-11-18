namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Why a library of the admin keeps the plugin from making or adopting one of its libraries (D4, D30).
/// </summary>
public enum LibraryConflictReason
{
    /// <summary>It holds a parent folder of the paths.</summary>
    ParentPath,

    /// <summary>It holds the paths with another type.</summary>
    OtherType,

    /// <summary>It holds the paths together with others, or only some of them.</summary>
    OtherPaths,

    /// <summary>Another library holds exactly the same paths with the same type.</summary>
    SamePaths
}
