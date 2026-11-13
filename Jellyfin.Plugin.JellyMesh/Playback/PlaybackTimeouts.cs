using System;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The times of a playback. The two client-facing timeouts exist because no client has one: without them a dead remote
/// is a spinner that never ends. Those marked as a default are settings, read through <see cref="PlaybackTimes"/>. The
/// others stay fixed: they only bound cleanup work that no administrator has a reason to tune.
/// </summary>
public static class PlaybackTimeouts
{
    /// <summary>
    /// Default limit on the <c>PlaybackInfo</c> call to a remote.
    /// </summary>
    public static readonly TimeSpan RemotePlaybackInfo = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Default limit on opening a stream: a playlist, a segment, or the first byte of a file.
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
    /// Default time without requests after which the transcode of a session is closed. A finished transcode never expires
    /// by itself on a remote.
    /// </summary>
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How often the sessions are checked.
    /// </summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the sessions are checked for a client that went, so that a JellyMesh remote hears of it right after the
    /// grace.
    /// </summary>
    public static readonly TimeSpan AbandonCheck = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often a stream slot is renewed on a JellyMesh remote while a client reads. Well under the idle time of a slot
    /// there, so that a lost renewal costs nothing.
    /// </summary>
    public static readonly TimeSpan SlotRenewal = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a refusal of the file of a stub at the stream limit explains a failure of the local transcode reading it.
    /// ffmpeg dies at once when its input is refused, so this only has to cover the way back to the client.
    /// </summary>
    public static readonly TimeSpan FileRefusalMemory = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a source that stopped serving a user during playback goes after the others for that user and title, and how
    /// long its failure counts towards <see cref="PlaybackLimits.Reconnections"/> (row 57).
    /// </summary>
    public static readonly TimeSpan FailedSourceMemory = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after a pause a break of the remote stream is put down to the pause and not to the source (row 273): what
    /// was held on the way drains first, and a remote or a proxy between can close a connection left unread meanwhile.
    /// </summary>
    public static readonly TimeSpan AfterClientPause = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Default time without requests after which a session is forgotten. Until then a paused player can resume with its
    /// URLs: the remote restarts ffmpeg at the requested segment.
    /// </summary>
    public static readonly TimeSpan SessionRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// Default time the transcode policy of a remote is kept.
    /// </summary>
    public static readonly TimeSpan PolicyRetention = TimeSpan.FromMinutes(5);
}
