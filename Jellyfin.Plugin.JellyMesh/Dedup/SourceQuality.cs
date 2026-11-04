namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The quality of one playable version, read from its default video stream (plan §4.3).
/// </summary>
/// <param name="Height">The real video height, <c>MediaStream.Height</c>. Missing counts as 0.</param>
/// <param name="IsHdr">Whether <c>MediaStream.VideoRange</c> is HDR, which covers Dolby Vision, HDR10+ and HDR10.</param>
/// <param name="BitRate">
/// The video <c>MediaStream.BitRate</c> in bit/s, as Jellyfin reports it and uncorrected, even though it is an
/// estimate for MKV (plan §10). Missing counts as 0.
/// </param>
/// <param name="Codec">The <c>MediaStream.Codec</c> as Jellyfin reports it, such as <c>h264</c>, <c>hevc</c> or <c>av1</c>.</param>
public sealed record SourceQuality(int? Height, bool IsHdr, int? BitRate, string? Codec);
