using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A new item that takes watch state from an old one.
/// </summary>
/// <param name="ItemId">The Jellyfin item.</param>
/// <param name="ServerId">The server of the stub, null for the local copy.</param>
public sealed record WatchStateTarget(Guid ItemId, Guid? ServerId);
