namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// One playable version of a catalog entry: one <c>MediaSources</c> entry, as read from the remote.
/// </summary>
public sealed record IndexedVersion
{
    /// <summary>Gets the media source ID. With the server and the item, it is the key of a source.</summary>
    public required string MediaSourceId { get; init; }

    /// <summary>Gets the version name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the container.</summary>
    public string? Container { get; init; }

    /// <summary>Gets the size in bytes.</summary>
    public long? Size { get; init; }

    /// <summary>Gets the container bitrate in bit/s.</summary>
    public int? ContainerBitRate { get; init; }

    /// <summary>Gets the duration.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets the tag that changes with the file.</summary>
    public string? ETag { get; init; }

    /// <summary>Gets the protocol, <c>Http</c> for a <c>.strm</c>.</summary>
    public string? Protocol { get; init; }

    /// <summary>Gets a value indicating whether the source is remote from its server, as a <c>.strm</c> is.</summary>
    public bool IsRemote { get; init; }

    /// <summary>Gets the height of the video stream.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the width of the video stream.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the codec of the video stream.</summary>
    public string? Codec { get; init; }

    /// <summary>Gets the bitrate of the video stream as Jellyfin reports it, uncorrected (plan §4.3).</summary>
    public int? BitRate { get; init; }

    /// <summary>Gets the range of the video stream, <c>SDR</c> or <c>HDR</c>.</summary>
    public string? VideoRange { get; init; }

    /// <summary>Gets the precise range of the video stream, such as <c>HDR10</c>.</summary>
    public string? VideoRangeType { get; init; }
}
