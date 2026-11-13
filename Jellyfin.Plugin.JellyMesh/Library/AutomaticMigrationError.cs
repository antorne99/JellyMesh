namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Why the automatic migration stopped at its step (Q57). It names no path.
/// </summary>
public enum AutomaticMigrationError
{
    /// <summary>No error.</summary>
    None,

    /// <summary>The libraries could not be read.</summary>
    ReadFailed,

    /// <summary>A server folder could not be added to a library.</summary>
    AddFailed,

    /// <summary>Some server folders are in no library, since no library holds the old path of their kind (Q215).</summary>
    FoldersInNoLibrary,

    /// <summary>A server folder left a library after it was added, so the old titles cannot be carried.</summary>
    NewPathsLeft,

    /// <summary>An old path could not be removed from a library.</summary>
    RemoveFailed,

    /// <summary>A step failed for another reason.</summary>
    Failed,

    /// <summary>
    /// The registry has no old title but the old folders still hold stubs, or could not be read, so the old paths stay
    /// (§10 row 371).
    /// </summary>
    OldStubsLeft
}
