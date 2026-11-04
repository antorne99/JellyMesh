using System;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Where and as which device to reach a remote.
/// </summary>
/// <param name="BaseUrl">The normalized base URL.</param>
/// <param name="DeviceId">The device identifier, stable per server: a login logs out earlier sessions of the same user and device.</param>
public sealed record RemoteEndpoint(Uri BaseUrl, string DeviceId);
