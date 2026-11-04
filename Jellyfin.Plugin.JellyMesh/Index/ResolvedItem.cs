using System;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// One item on one remote server.
/// </summary>
/// <param name="ServerId">The server.</param>
/// <param name="ItemId">The item on the server.</param>
public sealed record ResolvedItem(Guid ServerId, string ItemId);
