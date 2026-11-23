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
    Series,

    /// <summary>A music video, one title like a film (row 454).</summary>
    MusicVideo,

    /// <summary>A music track, known as in the dedup of music (row 453).</summary>
    Track,

    /// <summary>One file of an audiobook.</summary>
    AudioBook,

    /// <summary>An ebook, PDF or comic.</summary>
    Book,

    /// <summary>A home video.</summary>
    HomeVideo
}
