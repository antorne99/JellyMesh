using System.Net.Http;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A stream to open on a remote.
/// </summary>
/// <param name="Method">GET or HEAD.</param>
/// <param name="Path">The path and query.</param>
/// <param name="Range">The <c>Range</c> header of the client, if any.</param>
public sealed record RemoteStreamRequest(HttpMethod Method, string Path, string? Range);
