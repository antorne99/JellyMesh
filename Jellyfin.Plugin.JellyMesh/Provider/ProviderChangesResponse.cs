using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A page of the change feed.
/// </summary>
/// <param name="Reset">Whether the reader has to read everything.</param>
/// <param name="Cursor">Where the next read starts from.</param>
/// <param name="HasMore">Whether more changes follow.</param>
/// <param name="Changes">The items that changed and the peer may see.</param>
public sealed record ProviderChangesResponse(bool Reset, string Cursor, bool HasMore, IReadOnlyList<ProviderChange> Changes);
