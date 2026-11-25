using System;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// How the copy of a remote stream ended, with how much went to the client and for how long.
/// </summary>
/// <param name="End">How it ended.</param>
/// <param name="Bytes">The bytes that went to the client.</param>
/// <param name="Elapsed">How long the copy ran.</param>
public readonly record struct RelayResult(RelayEnd End, long Bytes, TimeSpan Elapsed)
{
    /// <summary>
    /// Gets what broke the copy, null when nothing did.
    /// </summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// Gets a value indicating whether the remote stopped serving, by breaking or going silent: not the client, nor the
    /// end, nor a break soon after a pause of the client.
    /// </summary>
    public bool CountsAsFailure => End is RelayEnd.RemoteStalled or RelayEnd.RemoteBroke;

    /// <summary>
    /// Gets a value indicating whether the copy ended for a reason worth a line in the log.
    /// </summary>
    public bool IsAbnormal => End is not (RelayEnd.Completed or RelayEnd.Head or RelayEnd.ClientLeft);
}
