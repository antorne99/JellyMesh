namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The kinds of catalog entry.
/// </summary>
public enum IndexedItemKind
{
    /// <summary>A film.</summary>
    Movie,

    /// <summary>A series. Holds the IDs and the metadata its episodes refer to.</summary>
    Series,

    /// <summary>A season.</summary>
    Season,

    /// <summary>An episode.</summary>
    Episode
}
