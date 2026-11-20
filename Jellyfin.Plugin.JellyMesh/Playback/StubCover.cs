namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The stubs a stock route of the guard covers.
/// </summary>
internal enum StubCover
{
    /// <summary>
    /// Every stub, by its URL or by its path: video, track or book.
    /// </summary>
    Any,

    /// <summary>
    /// Audio stubs only: tracks and audiobooks.
    /// </summary>
    Audio
}
