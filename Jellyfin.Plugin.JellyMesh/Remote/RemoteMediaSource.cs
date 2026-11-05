using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// An entry of <c>BaseItemDto.MediaSources</c>. Like <see cref="RemoteItem"/>, a whitelist.
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

    /// <summary>
    /// Gets the path, the URL of the target for a <c>.strm</c>. Read only to recognise the stub of another JellyMesh
    /// (decision 11): it can hold a remote address, so the mapper never copies it.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>Gets the transport stream timestamp type.</summary>
    public string? Timestamp { get; init; }

    /// <summary>Gets the 3D format.</summary>
    public string? Video3DFormat { get; init; }

    /// <summary>Gets the quality worked out by a JellyMesh provider, null from a stock remote.</summary>
    public RemoteQuality? Quality { get; init; }

    /// <summary>Gets the streams.</summary>
    public IReadOnlyList<RemoteStreamInfo>? MediaStreams { get; init; }
}
