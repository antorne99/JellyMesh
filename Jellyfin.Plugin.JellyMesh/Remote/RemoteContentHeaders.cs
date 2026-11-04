namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The headers of a remote stream that are passed on to a client.
/// </summary>
/// <param name="ContentType">The content type.</param>
/// <param name="ContentLength">The length.</param>
/// <param name="ContentRange">The range.</param>
/// <param name="AcceptRanges">The accepted ranges.</param>
public sealed record RemoteContentHeaders(string? ContentType, long? ContentLength, string? ContentRange, string? AcceptRanges);
