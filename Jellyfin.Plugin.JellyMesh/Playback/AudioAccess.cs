using System;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Who asks for a track stub on a stock audio route.
/// </summary>
/// <param name="UserId">The local user.</param>
/// <param name="DeviceId">The device of the local access.</param>
/// <param name="Token">The local token, which goes into the URLs the client receives.</param>
/// <param name="ItemId">The track.</param>
public sealed record AudioAccess(Guid UserId, string DeviceId, string Token, Guid ItemId);
