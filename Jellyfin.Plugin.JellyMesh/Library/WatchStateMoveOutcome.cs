using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a run of the watch state carry did with one move.
/// </summary>
/// <param name="Move">The move.</param>
/// <param name="Status">What was done.</param>
/// <param name="SkippedUsers">The users not done, empty unless the status is <see cref="WatchStateMoveStatus.UsersLeft"/> or <see cref="WatchStateMoveStatus.Failed"/>.</param>
public sealed record WatchStateMoveOutcome(WatchStateMove Move, WatchStateMoveStatus Status, IReadOnlyList<WatchStateUserSkip> SkippedUsers);
