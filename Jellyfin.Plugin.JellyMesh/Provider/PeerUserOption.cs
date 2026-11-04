using System;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A user that can be chosen for a connected server.
/// </summary>
/// <param name="Id">The user.</param>
/// <param name="Name">The name.</param>
public sealed record PeerUserOption(Guid Id, string Name);
