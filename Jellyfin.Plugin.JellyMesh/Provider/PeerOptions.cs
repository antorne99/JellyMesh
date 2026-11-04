using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// What the configuration page offers for a connected server.
/// </summary>
/// <param name="Users">The non-admin users.</param>
/// <param name="Libraries">The film and series libraries.</param>
public sealed record PeerOptions(IReadOnlyList<PeerUserOption> Users, IReadOnlyList<ProviderLibrary> Libraries);
