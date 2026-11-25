namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The kind of a library the plugin creates for a server. Kept by name, so new kinds go at the end.
/// </summary>
public enum ServerLibraryKind
{
    /// <summary>The Film library.</summary>
    Movies,

    /// <summary>The Serie library.</summary>
    Series,

    /// <summary>The Musica library.</summary>
    Music,

    /// <summary>The Video musicali library.</summary>
    MusicVideos,

    /// <summary>The Video domestici library, with the photos.</summary>
    HomeVideos,

    /// <summary>The Libri library, with the audiobooks.</summary>
    Books,

    /// <summary>The library of a mixed remote library, with no type of its own [D14].</summary>
    Mixed
}
