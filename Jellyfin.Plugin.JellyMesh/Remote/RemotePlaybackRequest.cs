using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// What a client asked for, addressed to an item on a remote.
/// </summary>
public sealed record RemotePlaybackRequest
{
    /// <summary>Gets the item on the remote.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the version on the remote. Without it the remote ignores the stream indexes.</summary>
    public required string MediaSourceId { get; init; }

    /// <summary>Gets the device profile of the real client, null if it sent none.</summary>
    public DeviceProfile? DeviceProfile { get; init; }

    /// <summary>Gets the audio stream, in the numbering of the remote.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the subtitle stream, in the numbering of the remote.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Gets the start position.</summary>
    public long? StartTimeTicks { get; init; }

    /// <summary>Gets the bitrate limit of the client.</summary>
    public int? MaxStreamingBitrate { get; init; }

    /// <summary>Gets the channel limit of the client.</summary>
    public int? MaxAudioChannels { get; init; }

    /// <summary>Gets whether the client allows direct play.</summary>
    public bool? EnableDirectPlay { get; init; }

    /// <summary>Gets whether the client allows direct stream.</summary>
    public bool? EnableDirectStream { get; init; }

    /// <summary>Gets whether the client allows transcoding.</summary>
    public bool? EnableTranscoding { get; init; }

    /// <summary>Gets whether the client allows copying the video.</summary>
    public bool? AllowVideoStreamCopy { get; init; }

    /// <summary>Gets whether the client allows copying the audio.</summary>
    public bool? AllowAudioStreamCopy { get; init; }

    /// <summary>Gets whether subtitles are burned in whenever the remote transcodes.</summary>
    public bool? AlwaysBurnInSubtitleWhenTranscoding { get; init; }

    /// <summary>
    /// Gets the play session on a JellyMesh remote that this request replaces, so that its slot passes to the new one.
    /// </summary>
    public string? ReplacesPlaySessionId { get; init; }
}
