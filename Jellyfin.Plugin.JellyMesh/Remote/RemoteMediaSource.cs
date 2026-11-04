using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// An entry of <c>BaseItemDto.MediaSources</c>. Like <see cref="RemoteItem"/>, a whitelist with no path or URL.
/// </summary>
public sealed record RemoteMediaSource
{
    /// <summary>Gets the media source ID.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the version name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the container.</summary>
    public string? Container { get; init; }

    /// <summary>Gets the size in bytes.</summary>
    public long? Size { get; init; }

    /// <summary>Gets the container bitrate in bit/s.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Gets the duration.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets the tag that changes with the file.</summary>
    public string? ETag { get; init; }

    /// <summary>Gets the protocol, <c>Http</c> for a <c>.strm</c>.</summary>
    public string? Protocol { get; init; }

    /// <summary>Gets a value indicating whether the source is remote from its server, as a <c>.strm</c> is.</summary>
    public bool IsRemote { get; init; }

    /// <summary>Gets the streams.</summary>
    public IReadOnlyList<RemoteStreamInfo>? MediaStreams { get; init; }
}
