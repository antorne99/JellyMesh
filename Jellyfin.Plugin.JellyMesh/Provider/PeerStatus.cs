using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A connected server, on the configuration page.
/// </summary>
/// <param name="UserId">The dedicated user.</param>
/// <param name="UserName">The name of that user.</param>
/// <param name="SharedLibraryIds">The shared libraries.</param>
/// <param name="MaxStreams">The stream limit.</param>
/// <param name="ActiveStreams">The streams in use.</param>
public sealed record PeerStatus(Guid UserId, string UserName, IReadOnlyList<string> SharedLibraryIds, int MaxStreams, int ActiveStreams);
