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

    /// <summary>Gets the name of the title; an episode's series name.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the year of the title; an episode's series year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the provider IDs of the film, music video or series; an episode's series IDs.</summary>
    public ContentIds Ids { get; init; } = ContentIds.None;

    /// <summary>Gets the original title of a film or book.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets the duration of a film, track, audiobook file or home video.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets the plot of a film or book.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the genres of a film or book.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Gets the studios of a film or book.</summary>
    public IReadOnlyList<string> Studios { get; init; } = [];

    /// <summary>Gets the cast and crew of a film without IDs, or the people of a book.</summary>
    public IReadOnlyList<string> People { get; init; } = [];

    /// <summary>Gets the artists of a music video, track or audiobook file.</summary>
    public IReadOnlyList<string> Artists { get; init; } = [];

    /// <summary>Gets the album artists of a track or audiobook file.</summary>
    public IReadOnlyList<string> AlbumArtists { get; init; } = [];

    /// <summary>Gets the album of a track or audiobook file.</summary>
    public string? Album { get; init; }

    /// <summary>Gets the series of a book.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets every provider ID of a track, audiobook file, book or home video, without the mark of the plugin.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the path of a track, audiobook file, book or home video below its type folder, with <c>/</c>.</summary>
    public string? Place { get; init; }

    /// <summary>Gets a value indicating whether an audiobook file was the only one of its book.</summary>
    public bool IsOnlyFile { get; init; }

    /// <summary>Gets the season of an episode, or the disc of a track or audiobook file.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the number of an episode, track, audiobook file or book.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets the last episode of a file that holds several.</summary>
    public int? EpisodeNumberEnd { get; init; }

    /// <summary>Gets when the record was saved, in UTC. It goes after 90 days unused.</summary>
    public required DateTime SavedUtc { get; init; }

    /// <summary>
    /// Gets the copies of the manual merges the title was in when it was kept, so that any copy left finds it (D33).
    /// </summary>
    public IReadOnlyList<MergedCopy> Copies { get; init; } = [];

    /// <summary>Gets the state of each user.</summary>
    public IReadOnlyList<KeptUserState> Users { get; init; } = [];
}
