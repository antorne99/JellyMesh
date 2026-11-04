namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A stream of a <see cref="RemoteMediaSource"/>. A whitelist: <c>Path</c> and <c>DeliveryUrl</c> are left out on purpose,
/// because an external subtitle has a remote path there.
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
    bool IsDefault)
{
    /// <summary>Gets the index in the remote layout.</summary>
    public int? Index { get; init; }

    /// <summary>Gets the language.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the codec profile.</summary>
    public string? Profile { get; init; }

    /// <summary>Gets the codec level.</summary>
    public double? Level { get; init; }

    /// <summary>Gets the bit depth.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Gets the aspect ratio.</summary>
    public string? AspectRatio { get; init; }

    /// <summary>Gets the average frame rate.</summary>
    public float? AverageFrameRate { get; init; }

    /// <summary>Gets the real frame rate.</summary>
    public float? RealFrameRate { get; init; }

    /// <summary>Gets the pixel format.</summary>
    public string? PixelFormat { get; init; }

    /// <summary>Gets a value indicating whether the stream is AVC.</summary>
    public bool? IsAVC { get; init; }

    /// <summary>Gets a value indicating whether the stream is anamorphic.</summary>
    public bool? IsAnamorphic { get; init; }

    /// <summary>Gets a value indicating whether the stream is interlaced.</summary>
    public bool IsInterlaced { get; init; }

    /// <summary>Gets the reference frames.</summary>
    public int? RefFrames { get; init; }

    /// <summary>Gets the codec tag.</summary>
    public string? CodecTag { get; init; }

    /// <summary>Gets the NAL length size.</summary>
    public string? NalLengthSize { get; init; }

    /// <summary>Gets the time base.</summary>
    public string? TimeBase { get; init; }

    /// <summary>Gets the codec time base.</summary>
    public string? CodecTimeBase { get; init; }

    /// <summary>Gets the rotation.</summary>
    public int? Rotation { get; init; }

    /// <summary>Gets the comment.</summary>
    public string? Comment { get; init; }

    /// <summary>Gets the audio channels.</summary>
    public int? Channels { get; init; }

    /// <summary>Gets the channel layout.</summary>
    public string? ChannelLayout { get; init; }

    /// <summary>Gets the sample rate.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Gets a value indicating whether the stream is forced.</summary>
    public bool IsForced { get; init; }

    /// <summary>Gets a value indicating whether the stream is for the hearing impaired.</summary>
    public bool IsHearingImpaired { get; init; }

    /// <summary>Gets a value indicating whether the stream is in the original language.</summary>
    public bool IsOriginal { get; init; }

    /// <summary>Gets a value indicating whether the stream is a separate file on the remote.</summary>
    public bool IsExternal { get; init; }

    /// <summary>Gets the color range.</summary>
    public string? ColorRange { get; init; }

    /// <summary>Gets the color transfer.</summary>
    public string? ColorTransfer { get; init; }

    /// <summary>Gets the color primaries.</summary>
    public string? ColorPrimaries { get; init; }

    /// <summary>Gets the color space.</summary>
    public string? ColorSpace { get; init; }

    /// <summary>Gets a value indicating whether HDR10+ data is present.</summary>
    public bool? Hdr10PlusPresentFlag { get; init; }

    /// <summary>Gets the Dolby Vision major version.</summary>
    public int? DvVersionMajor { get; init; }

    /// <summary>Gets the Dolby Vision minor version.</summary>
    public int? DvVersionMinor { get; init; }

    /// <summary>Gets the Dolby Vision profile.</summary>
    public int? DvProfile { get; init; }

    /// <summary>Gets the Dolby Vision level.</summary>
    public int? DvLevel { get; init; }

    /// <summary>Gets the Dolby Vision RPU flag.</summary>
    public int? RpuPresentFlag { get; init; }

    /// <summary>Gets the Dolby Vision EL flag.</summary>
    public int? ElPresentFlag { get; init; }

    /// <summary>Gets the Dolby Vision BL flag.</summary>
    public int? BlPresentFlag { get; init; }

    /// <summary>Gets the Dolby Vision compatibility ID.</summary>
    public int? DvBlSignalCompatibilityId { get; init; }
}
