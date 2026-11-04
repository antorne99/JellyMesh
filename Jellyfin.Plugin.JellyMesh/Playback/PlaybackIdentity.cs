using System;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Who plays what, on the local server.
/// </summary>
/// <param name="UserId">The local user.</param>
/// <param name="DeviceId">The local device.</param>
/// <param name="ItemId">The local item.</param>
/// <param name="StubId">The stub.</param>
public sealed record PlaybackIdentity(Guid UserId, string DeviceId, Guid ItemId, string StubId);
