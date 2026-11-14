using System;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// One local user's watch state of a title whose server was removed (Q412). Keyed by the local user only.
/// </summary>
/// <param name="UserId">The local user.</param>
/// <param name="Played">Whether the title was played.</param>
/// <param name="PlayCount">How many times it was played.</param>
/// <param name="PlaybackPositionTicks">The resume position.</param>
/// <param name="LastPlayedUtc">When it was last played, in UTC.</param>
/// <param name="IsFavorite">Whether it is a favorite.</param>
/// <param name="Rating">The user's rating.</param>
public sealed record KeptUserState(
    Guid UserId,
    bool Played,
    int PlayCount,
    long PlaybackPositionTicks,
    DateTime? LastPlayedUtc,
    bool IsFavorite,
    double? Rating);
