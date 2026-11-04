namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The kinds of content that are deduplicated (plan §1.1).
/// </summary>
public enum ContentKind
{
    /// <summary>A film.</summary>
    Movie,

    /// <summary>A series episode. Series and seasons are not items of their own here.</summary>
    Episode,
}
