namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A stream of a <see cref="RemoteMediaSource"/>. Only what decides quality is read.
/// </summary>
/// <param name="Type">The kind of stream, such as <c>Video</c>.</param>
/// <param name="Codec">The codec.</param>
/// <param name="Height">The height.</param>
/// <param name="Width">The width.</param>
/// <param name="BitRate">The bitrate in bit/s.</param>
/// <param name="VideoRange">The range, <c>SDR</c> or <c>HDR</c>.</param>
/// <param name="VideoRangeType">The precise range, such as <c>HDR10</c>.</param>
/// <param name="IsDefault">Whether it is the default stream.</param>
public sealed record RemoteStreamInfo(
    string? Type,
    string? Codec,
    int? Height,
    int? Width,
    int? BitRate,
    string? VideoRange,
    string? VideoRangeType,
    bool IsDefault);
