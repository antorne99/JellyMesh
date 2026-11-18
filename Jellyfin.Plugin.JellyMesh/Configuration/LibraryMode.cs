namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Which local libraries hold the type folders of a server (§10 row 437).
/// </summary>
public enum LibraryMode
{
    /// <summary>No library: the admin adds the type folders to libraries by hand.</summary>
    None,

    /// <summary>One library per type, "{Server} - Film" and so on, with the type folders of every remote library.</summary>
    ByType,

    /// <summary>One library per imported remote library, "{Server} - {Remote library}", with its type folders.</summary>
    ByRemoteLibrary
}
