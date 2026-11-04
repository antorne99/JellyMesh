using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The peer behind a request.
/// </summary>
/// <param name="Grant">What it may do.</param>
/// <param name="User">The dedicated local user.</param>
public sealed record PeerCaller(PeerGrant Grant, User User);
