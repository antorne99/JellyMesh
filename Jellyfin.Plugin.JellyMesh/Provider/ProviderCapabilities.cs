namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The answer of the capability endpoint.
/// </summary>
/// <param name="ProtocolVersion">The protocol version.</param>
public sealed record ProviderCapabilities(int ProtocolVersion);
