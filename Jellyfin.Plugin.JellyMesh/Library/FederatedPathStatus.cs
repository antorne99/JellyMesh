using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// How a subfolder of the federated folder is linked.
/// </summary>
/// <param name="Link">The link.</param>
/// <param name="Libraries">The libraries that <paramref name="Link"/> is about.</param>
public sealed record FederatedPathStatus(FederatedPathLink Link, IReadOnlyList<string> Libraries);
