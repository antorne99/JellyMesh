using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// A <c>PlaybackInfo</c> on a stub.
/// </summary>
public sealed record PlaybackOpenRequest
{
    /// <summary>Gets who plays what.</summary>
    public required PlaybackIdentity Identity { get; init; }

    /// <summary>Gets the stub.</summary>
    public required StubEntry Stub { get; init; }

    /// <summary>Gets the other versions the user sees, best first, tried when the stub cannot serve (D22).</summary>
    public IReadOnlyList<PlaybackCandidate> Fallbacks { get; init; } = [];

    /// <summary>Gets the local token of the caller.</summary>
    public required string LocalToken { get; init; }

    /// <summary>Gets the device profile of the client, null if it sent none.</summary>
    public DeviceProfile? DeviceProfile { get; init; }

    /// <summary>Gets the audio stream.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Gets the subtitle stream.</summary>
    public int? SubtitleStreamIndex { get; init; }

    /// <summary>Gets the start position.</summary>
    public long? StartTimeTicks { get; init; }

    /// <summary>Gets the bitrate limit.</summary>
    public int? MaxStreamingBitrate { get; init; }

    /// <summary>Gets the channel limit.</summary>
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

    /// <summary>Gets whether subtitles are always burned in.</summary>
    public bool? AlwaysBurnInSubtitleWhenTranscoding { get; init; }
}
