using System.Net.Http;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A stream to open on a remote.
/// </summary>
/// <param name="Method">GET or HEAD.</param>
/// <param name="Path">The path and query.</param>
/// <param name="Range">The <c>Range</c> header of the client, if any.</param>
/// <param name="IsProbe">Whether to tell a JellyMesh remote that this only proves the stream opens.</param>
/// <param name="Device">The hash of the device a track plays on, for a JellyMesh remote only.</param>
public sealed record RemoteStreamRequest(HttpMethod Method, string Path, string? Range, bool IsProbe = false, string? Device = null);
