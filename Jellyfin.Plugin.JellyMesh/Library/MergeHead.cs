using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// An item that showed a title as an entity of its own before a manual merge.
/// </summary>
/// <param name="ItemId">The Jellyfin item.</param>
/// <param name="Stub">The stub of the item, null for a local copy.</param>
public sealed record MergeHead(Guid ItemId, StubEntry? Stub);
