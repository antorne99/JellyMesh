namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Which part of a user's watch state a carry takes (decision 155).
/// </summary>
public enum WatchStateCarryMode
{
    /// <summary>The first carry from an old item: everything.</summary>
    First,

    /// <summary>A carry repeated because the old item was watched again: position, play count and played only.</summary>
    Repeat
}
