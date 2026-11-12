using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A new version of a title that takes watch state from an old item.
/// </summary>
/// <param name="ItemId">The Jellyfin item of the version.</param>
/// <param name="ServerId">The server of the stub, null for the local copy.</param>
public sealed record WatchStateTarget(Guid ItemId, Guid? ServerId);
