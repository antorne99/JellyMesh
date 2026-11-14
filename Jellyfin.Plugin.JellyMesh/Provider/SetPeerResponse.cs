using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// What a save left out of what is shared with a connected server.
/// </summary>
/// <param name="DroppedLibraryIds">The libraries no longer offered, sent or saved before, that are no longer shared.</param>
public sealed record SetPeerResponse(IReadOnlyList<string> DroppedLibraryIds);
