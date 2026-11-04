namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The end of the chain of §3.1.
/// </summary>
public enum PlaybackOutcomeKind
{
    /// <summary>
    /// A remote serves the stream through a session.
    /// </summary>
    Remote,

    /// <summary>
    /// No remote can serve it to this client, but one can give the file: the local server transcodes it.
    /// </summary>
    LocalTranscode,

    /// <summary>
    /// Nothing can serve it.
    /// </summary>
    Unavailable
}
