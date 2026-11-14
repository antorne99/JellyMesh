namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What an addition or a removal of a federated folder in a library did.
/// </summary>
public enum LibraryPathOutcome
{
    /// <summary>The library now has the folder, or no longer has it.</summary>
    Done,

    /// <summary>The library already had the folder, or never had it: nothing was called.</summary>
    Unchanged,

    /// <summary>No library has that name.</summary>
    NoLibrary,

    /// <summary>The path is not a folder of the federated layout, so the plugin may not touch it.</summary>
    Refused,

    /// <summary>The libraries could not be read, the call threw, or it left the library as it was.</summary>
    Failed
}
