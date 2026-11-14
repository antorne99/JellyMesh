namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The kind of title whose watch state is kept after its server was removed (Q412).
/// </summary>
public enum KeptTitleKind
{
    /// <summary>A film.</summary>
    Movie,

    /// <summary>An episode, known by its series, season and number.</summary>
    Episode,

    /// <summary>A series, which holds only favorite and rating.</summary>
    Series
}
