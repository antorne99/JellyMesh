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
    Episode,

    /// <summary>A music track.</summary>
    Audio,

    /// <summary>A music video.</summary>
    MusicVideo,

    /// <summary>A home video.</summary>
    Video,

    /// <summary>A photo.</summary>
    Photo,

    /// <summary>A photo album of a home video library.</summary>
    PhotoAlbum,

    /// <summary>A folder of a home video library.</summary>
    Folder,

    /// <summary>An ebook, a PDF or a comic.</summary>
    Book,

    /// <summary>An audiobook, or one part of an audiobook in several files.</summary>
    AudioBook
}
