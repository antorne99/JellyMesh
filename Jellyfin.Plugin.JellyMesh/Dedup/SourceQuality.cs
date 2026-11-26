namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The quality of one playable version, read from its default video stream (plan §4.3), or from its default audio stream
/// for a track (row 453).
/// </summary>
/// <param name="Height">The real video height, <c>MediaStream.Height</c>. Missing counts as 0.</param>
/// <param name="IsHdr">Whether <c>MediaStream.VideoRange</c> is HDR, which covers Dolby Vision, HDR10+ and HDR10.</param>
/// <param name="BitRate">
/// The video <c>MediaStream.BitRate</c> in bit/s, as Jellyfin reports it and uncorrected, even though it is an
/// estimate for MKV (plan §10). Missing counts as 0. For a track, the audio bitrate.
/// </param>
/// <param name="Codec">The <c>MediaStream.Codec</c> as Jellyfin reports it, such as <c>h264</c>, <c>hevc</c> or <c>av1</c>.</param>
public sealed record SourceQuality(int? Height, bool IsHdr, int? BitRate, string? Codec)
{
    /// <summary>Gets a value indicating whether the range was read. If not, <see cref="IsHdr"/> is false without being known.</summary>
    public bool IsRangeKnown { get; init; } = true;

    /// <summary>Gets the bit depth of the audio stream of a track. Missing counts as 0.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Gets the sample rate of the audio stream of a track, in Hz. Missing counts as 0.</summary>
    public int? SampleRate { get; init; }

    /// <summary>
    /// Makes the quality of a track from its audio stream.
    /// </summary>
    /// <param name="codec">The codec, such as <c>flac</c> or <c>pcm_s16le</c>.</param>
    /// <param name="bitDepth">The bit depth.</param>
    /// <param name="sampleRate">The sample rate in Hz.</param>
    /// <param name="bitRate">The bitrate in bit/s.</param>
    /// <returns>The quality.</returns>
    public static SourceQuality Audio(string? codec, int? bitDepth, int? sampleRate, int? bitRate) =>
        new(null, false, bitRate, codec) { IsRangeKnown = false, BitDepth = bitDepth, SampleRate = sampleRate };
}
