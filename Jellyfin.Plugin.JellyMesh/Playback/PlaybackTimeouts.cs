using System;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The times of a playback. The two client-facing timeouts exist because no client has one: without them a dead remote
/// is a spinner that never ends.
/// </summary>
public static class PlaybackTimeouts
{
    /// <summary>
    /// Limit on the <c>PlaybackInfo</c> call to a remote.
    /// </summary>
    public static readonly TimeSpan RemotePlaybackInfo = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Limit on opening a stream: a playlist, a segment, or the first byte of a file.
    /// </summary>
    public static readonly TimeSpan StreamOpen = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Limit on closing a transcode on a remote.
    /// </summary>
    public static readonly TimeSpan RemoteStop = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Limit on closing all sessions at shutdown, so that a dead remote cannot hold the server up.
    /// </summary>
    public static readonly TimeSpan Shutdown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Time without requests after which the transcode of a session is closed. A finished transcode never expires by
    /// itself on a remote.
    /// </summary>
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How often the sessions are checked.
    /// </summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Time without requests after which a session is forgotten. Until then a paused player can resume with its URLs:
    /// the remote restarts ffmpeg at the requested segment.
    /// </summary>
    public static readonly TimeSpan SessionRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// How long the transcode policy of a remote is kept.
    /// </summary>
    public static readonly TimeSpan PolicyRetention = TimeSpan.FromMinutes(5);
}
