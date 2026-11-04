using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// A variant of a master playlist.
/// </summary>
/// <param name="Uri">The URI of its playlist.</param>
/// <param name="Codecs">The entries of its <c>CODECS</c> attribute.</param>
public sealed record HlsVariant(string Uri, IReadOnlyList<string> Codecs);
