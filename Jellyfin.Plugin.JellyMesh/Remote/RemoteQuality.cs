namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// What §4.3 compares of a version, worked out by the provider that owns the file. Untrusted text, like the rest of a
/// remote catalog.
/// </summary>
/// <param name="Height">The height of the video.</param>
/// <param name="Width">The width of the video.</param>
/// <param name="BitRate">The bitrate of the video stream as reported, in bit/s.</param>
/// <param name="Codec">The video codec.</param>
/// <param name="VideoRange">The video range, such as <c>HDR</c>.</param>
/// <param name="VideoRangeType">The video range type, such as <c>HDR10</c>.</param>
public sealed record RemoteQuality(int? Height, int? Width, int? BitRate, string? Codec, string? VideoRange, string? VideoRangeType);
