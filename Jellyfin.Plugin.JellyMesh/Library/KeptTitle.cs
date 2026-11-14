using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A title of a removed server with the watch state of its users, kept by content identity until the same content comes
/// back (Q412). Holds what the dedup of §4.1 compares, and no address or token.
/// </summary>
public sealed record KeptTitle
{
    /// <summary>Gets the record, zero until it is saved.</summary>
    public long Id { get; init; }

    /// <summary>Gets the library item the state was read from, so that a second save replaces the first.</summary>
    public required Guid OldItemId { get; init; }

    /// <summary>Gets the kind of title.</summary>
    public required KeptTitleKind Kind { get; init; }

    /// <summary>Gets the film's or series' name; an episode's series name.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the film's or series' year; an episode's series year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the provider IDs of the film or series; an episode's series IDs.</summary>
    public ContentIds Ids { get; init; } = ContentIds.None;

    /// <summary>Gets the original title of a film.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets the duration of a film.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets the plot of a film.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the genres of a film.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Gets the studios of a film.</summary>
    public IReadOnlyList<string> Studios { get; init; } = [];

    /// <summary>Gets the cast and crew of a film without IDs.</summary>
    public IReadOnlyList<string> People { get; init; } = [];

    /// <summary>Gets the season of an episode.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the number of an episode.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets the last episode of a file that holds several.</summary>
    public int? EpisodeNumberEnd { get; init; }

    /// <summary>Gets when the record was saved, in UTC. It goes after 90 days unused.</summary>
    public required DateTime SavedUtc { get; init; }

    /// <summary>Gets the state of each user.</summary>
    public IReadOnlyList<KeptUserState> Users { get; init; } = [];
}
