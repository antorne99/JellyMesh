using System;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Capabilities of a remote.
/// </summary>
/// <param name="Mode">The mode.</param>
/// <param name="ProtocolVersion">The JellyMesh protocol version, 0 for standard mode.</param>
public sealed record RemoteCapabilities(RemoteMode Mode, int ProtocolVersion);
