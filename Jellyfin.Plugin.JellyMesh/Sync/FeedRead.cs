using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// What the change feed of a JellyMesh remote gave.
/// </summary>
/// <param name="Reset">Whether the cursor could not be continued, so only a full read can catch up.</param>
/// <param name="Cursor">Where the next read starts from.</param>
/// <param name="Items">The entries that changed.</param>
public sealed record FeedRead(bool Reset, string Cursor, IReadOnlyList<IndexedItem> Items);
