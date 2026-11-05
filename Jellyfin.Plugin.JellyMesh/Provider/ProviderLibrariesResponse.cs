using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The libraries shared with a peer, in the shape of <c>/UserViews</c>.
/// </summary>
/// <param name="Items">The libraries.</param>
public sealed record ProviderLibrariesResponse(IReadOnlyList<ProviderLibrary> Items);
