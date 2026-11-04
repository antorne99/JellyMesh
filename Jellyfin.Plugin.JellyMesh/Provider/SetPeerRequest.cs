using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// What is shared with a connected server.
/// </summary>
/// <param name="SharedLibraryIds">The shared libraries.</param>
/// <param name="MaxStreams">The stream limit.</param>
public sealed record SetPeerRequest(IReadOnlyList<string> SharedLibraryIds, int MaxStreams);
