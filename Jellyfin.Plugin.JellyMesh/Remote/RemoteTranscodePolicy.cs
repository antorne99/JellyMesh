namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// What a remote says about transcoding, readable by a non-admin user. A remote never says it cannot transcode, so this
/// only rules out what its policy forbids.
/// </summary>
/// <param name="HardwareAccelerationType">The <c>HardwareAccelerationType</c> of the remote, null if unreadable.</param>
/// <param name="EnableVideoTranscoding">Whether the dedicated user may transcode video.</param>
/// <param name="EnableAudioTranscoding">Whether the dedicated user may transcode audio.</param>
/// <param name="EnableRemuxing">Whether the dedicated user may remux.</param>
public sealed record RemoteTranscodePolicy(
    string? HardwareAccelerationType,
    bool EnableVideoTranscoding,
    bool EnableAudioTranscoding,
    bool EnableRemuxing);
