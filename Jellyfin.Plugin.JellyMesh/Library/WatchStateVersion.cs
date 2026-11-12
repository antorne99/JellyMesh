using System;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// One user's data on a new version of a title, as the watch state carry sees it.
/// </summary>
/// <param name="ServerId">The server of the stub, null for the local copy.</param>
/// <param name="IsPrimary">Whether the version is the primary of its title (Q43).</param>
/// <param name="Data">The user's data on the version, changed in place.</param>
public sealed record WatchStateVersion(Guid? ServerId, bool IsPrimary, UserItemData Data);
