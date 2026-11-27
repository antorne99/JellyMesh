namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The kinds of content that are deduplicated (plan §1.1, row 453).
/// </summary>
public enum ContentKind
{
    /// <summary>A film.</summary>
    Movie,

    /// <summary>A series episode. Series and seasons are not items of their own here.</summary>
    Episode,

    /// <summary>A music video, one title like a film (row 454).</summary>
    MusicVideo,

    /// <summary>A music track (row 453). Tracks have no versions: each copy keeps its own stub.</summary>
    Track,
}
