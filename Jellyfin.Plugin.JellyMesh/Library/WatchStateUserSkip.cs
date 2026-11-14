using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A user for whom the watch state of a move was not carried.
/// </summary>
/// <param name="UserId">The local user.</param>
/// <param name="Reason">Why.</param>
public sealed record WatchStateUserSkip(Guid UserId, WatchStateUserSkipReason Reason);
