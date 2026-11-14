namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The step the automatic migration is at, saved so that a restart or a retry resumes there (Q56, Q57).
/// </summary>
public enum AutomaticMigrationStep
{
    /// <summary>Adding the server folders to each library on an old path.</summary>
    AddPaths,

    /// <summary>Waiting for the scan of the new paths.</summary>
    Scan,

    /// <summary>Carrying the watch state, then emptying the old folders.</summary>
    Carry,

    /// <summary>Removing the old paths from the libraries.</summary>
    RemoveOldPaths,

    /// <summary>Waiting for the scan after the removal.</summary>
    Finish,

    /// <summary>Done.</summary>
    Done
}
