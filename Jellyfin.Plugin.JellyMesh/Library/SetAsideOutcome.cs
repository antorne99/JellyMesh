namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What became of a rename the resume cannot trust (§10 rows 329, 357).
/// </summary>
public enum SetAsideOutcome
{
    /// <summary>
    /// Copied to the damaged file and dropped from the pending ones.
    /// </summary>
    SetAside,

    /// <summary>
    /// Dropped from the pending ones, but the damaged file could not be written.
    /// </summary>
    DroppedWithoutCopy,

    /// <summary>
    /// Still in the file of the pending ones, which cannot be read or written for now.
    /// </summary>
    Pending
}
