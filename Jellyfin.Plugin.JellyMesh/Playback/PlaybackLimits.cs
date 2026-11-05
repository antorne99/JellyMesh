using System;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// How much of a remote the clients of this server can use at once. They exist for what a client can multiply: each
/// <c>PlaybackInfo</c> walks a whole chain and leaves a transcode behind. None of them is near what a family watching a
/// few films ever reaches. Fixed values, not settings: nothing about a server tells an administrator how to tune them.
/// </summary>
public static class PlaybackLimits
{
    /// <summary>
    /// The most client-driven calls open on one server: <c>PlaybackInfo</c>, playlists and the opening of a stream. A start
    /// of playback is a handful of calls in a row, and a running player one or two.
    /// </summary>
    public const int CallsPerServer = 16;

    /// <summary>
    /// The most streams open on one server, a connection each for as long as they last. A film takes one, and an HLS player
    /// one or two for the time of a segment.
    /// </summary>
    public const int StreamsPerServer = 32;

    /// <summary>
    /// The most requests running in one session. An HLS player asks for two or three things at a time.
    /// </summary>
    public const int RequestsPerSession = 8;

    /// <summary>
    /// The most sessions of one user. Over it the least recently used ones not in use are closed, and if all are in use
    /// the new playback is refused. Well above the devices of an account shared by a family.
    /// </summary>
    public const int SessionsPerUser = 16;

    /// <summary>
    /// The most folders a remote can make one session remember. A film needs one for the segments and one per subtitle.
    /// </summary>
    public const int FoldersPerSession = 1024;

    /// <summary>
    /// The most kinds of file a remote can make one session remember. Numbered segments count as one.
    /// </summary>
    public const int FileShapesPerSession = 4096;

    /// <summary>
    /// The longest a call waits for a place among <see cref="CallsPerServer"/>. Below the shortest time decision 17 allows
    /// for a call, so that a busy server reads as busy and not as offline.
    /// </summary>
    public static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(2);
}
