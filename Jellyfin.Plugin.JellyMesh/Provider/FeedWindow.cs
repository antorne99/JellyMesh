using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// A stretch of the change feed.
/// </summary>
/// <param name="Reset">Whether the cursor cannot be continued, so the reader has to read everything.</param>
/// <param name="Cursor">Where the next read starts from.</param>
/// <param name="ItemIds">The items that changed, each once.</param>
/// <param name="HasMore">Whether more changes follow.</param>
public sealed record FeedWindow(bool Reset, string Cursor, IReadOnlyList<Guid> ItemIds, bool HasMore);
