namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The item types read from a remote catalog. Names are the <c>includeItemTypes</c> values of <c>/Items</c>.
/// </summary>
public enum RemoteItemType
{
    /// <summary>A film.</summary>
    Movie,

    /// <summary>A series.</summary>
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

    /// <summary>A photo album, a folder of a home video library that Jellyfin saw as one.</summary>
    PhotoAlbum,

    /// <summary>A folder of a home video library.</summary>
    Folder,

    /// <summary>An ebook, a PDF or a comic.</summary>
    Book,

    /// <summary>An audiobook, or one part of an audiobook in several files.</summary>
    AudioBook
}
