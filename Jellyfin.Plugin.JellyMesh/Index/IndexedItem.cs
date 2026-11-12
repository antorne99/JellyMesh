using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// One raw catalog entry of one server. Sources are identified by server, <see cref="ItemId"/> and
/// <see cref="IndexedVersion.MediaSourceId"/>; a content identity is never stored, because it depends on the whole
/// catalog. Decisions are worked out from these entries.
/// </summary>
/// <remarks>
/// Holds what a full <c>.nfo</c> needs (plan §3.1). Holds no path, no URL and no token: everything comes through the
/// whitelist of <see cref="Remote.RemoteItem"/>.
/// </remarks>
public sealed record IndexedItem
{
    /// <summary>Gets the item ID on the server.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the kind of entry.</summary>
    public required IndexedItemKind Kind { get; init; }

    /// <summary>Gets the remote library the entry was read from.</summary>
    public required string LibraryId { get; init; }

    /// <summary>Gets when the entry was last seen on the server.</summary>
    public required DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>Gets the name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the original title.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets the plot.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the taglines.</summary>
    public IReadOnlyList<string> Taglines { get; init; } = [];

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Gets the studios.</summary>
    public IReadOnlyList<string> Studios { get; init; } = [];

    /// <summary>Gets the tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the cast and crew.</summary>
    public IReadOnlyList<IndexedPerson> People { get; init; } = [];

    /// <summary>Gets the age rating.</summary>
    public string? OfficialRating { get; init; }

    /// <summary>Gets the community rating.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets the critic rating.</summary>
    public float? CriticRating { get; init; }

    /// <summary>Gets the release date.</summary>
    public DateTimeOffset? PremiereDate { get; init; }

    /// <summary>Gets the end date of a series.</summary>
    public DateTimeOffset? EndDate { get; init; }

    /// <summary>Gets the production year.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the status of a series.</summary>
    public string? Status { get; init; }

    /// <summary>
    /// Gets all the provider IDs of this entry, not only those used for identity: the series merge key needs
    /// <c>Custom</c>, <c>Tvdb</c> and <c>Imdb</c>. An episode finds the IDs of its series on the entry
    /// <see cref="SeriesId"/> points to.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the duration.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets when the item was added to the server.</summary>
    public DateTimeOffset? DateCreated { get; init; }

    /// <summary>Gets the tag that changes with the item.</summary>
    public string? Etag { get; init; }

    /// <summary>Gets the number of a season, or of an episode within its season.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>
    /// Gets the last episode of a file that holds several. Files written before this existed have none, and the next
    /// complete read fills it in.
    /// </summary>
    public int? IndexNumberEnd { get; init; }

    /// <summary>Gets the season number of an episode.</summary>
    public int? ParentIndexNumber { get; init; }

    /// <summary>Gets the series of a season or an episode.</summary>
    public string? SeriesId { get; init; }

    /// <summary>Gets the series name of a season or an episode.</summary>
    public string? SeriesName { get; init; }

    /// <summary>Gets the season of an episode.</summary>
    public string? SeasonId { get; init; }

    /// <summary>Gets the season name of an episode.</summary>
    public string? SeasonName { get; init; }

    /// <summary>Gets the image tags by image type.</summary>
    public IReadOnlyDictionary<string, string> ImageTags { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the backdrop image tags.</summary>
    public IReadOnlyList<string> BackdropImageTags { get; init; } = [];

    /// <summary>Gets the chapters of a film or an episode.</summary>
    public IReadOnlyList<IndexedChapter> Chapters { get; init; } = [];

    /// <summary>Gets the playable versions of a film or an episode.</summary>
    public IReadOnlyList<IndexedVersion> Versions { get; init; } = [];

    /// <summary>
    /// Gets the collection of a film, read only by a complete read of a stock server with collections turned on
    /// (decision 52). A read of changes keeps the one already known.
    /// </summary>
    public IndexedBoxSet? Collection { get; init; }
}
