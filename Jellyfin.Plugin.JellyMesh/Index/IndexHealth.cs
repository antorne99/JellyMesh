namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The state of the index file.
/// </summary>
public enum IndexHealth
{
    /// <summary>The index is sound, or there was none yet.</summary>
    Ok,

    /// <summary>The file was damaged and the previous copy was loaded. Every server is stale until it syncs again.</summary>
    RestoredFromBackup,

    /// <summary>No copy could be read, so the index is empty for lack of data and not because the catalogs are.</summary>
    Corrupt
}
