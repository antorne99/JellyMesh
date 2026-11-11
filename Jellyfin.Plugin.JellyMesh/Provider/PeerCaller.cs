using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The peer behind a request.
/// </summary>
/// <param name="Grant">What it may do.</param>
/// <param name="User">The dedicated local user.</param>
/// <param name="DeviceId">The device its authorization header names, null if none or not from a request.</param>
public sealed record PeerCaller(PeerGrant Grant, User User, string? DeviceId = null);
