namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// How a stream reaches the client.
/// </summary>
public enum PlaybackStreamKind
{
    /// <summary>
    /// HLS playlists and segments, with a transcode running on the remote.
    /// </summary>
    Hls,

    /// <summary>
    /// The file of the remote, with <c>Range</c>. Nothing runs on the remote.
    /// </summary>
    Direct,

    /// <summary>
    /// A progressive transcode on the remote.
    /// </summary>
    Progressive
}
