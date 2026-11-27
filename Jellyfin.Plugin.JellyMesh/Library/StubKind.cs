namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The kinds of stub.
/// </summary>
public enum StubKind
{
    /// <summary>A film.</summary>
    Movie,

    /// <summary>An episode.</summary>
    Episode,

    /// <summary>A music track.</summary>
    Track,

    /// <summary>A music video, in a title folder like a film.</summary>
    MusicVideo,

    /// <summary>A home video, in the folders of its remote library.</summary>
    HomeVideo,

    /// <summary>An ebook, PDF or comic: a placeholder with the real extension instead of a <c>.strm</c> (D47).</summary>
    Book,

    /// <summary>A photo: a 1x1 placeholder image with the real extension, among the home videos (D7, D45).</summary>
    Photo,

    /// <summary>One file of an audiobook, a <c>.strm</c> with no <c>.nfo</c>.</summary>
    AudioBook
}
