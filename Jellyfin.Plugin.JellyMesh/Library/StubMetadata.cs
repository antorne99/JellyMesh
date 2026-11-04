using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a <c>.nfo</c> says, and what is applied to an item that already exists, because Jellyfin does not read a changed
/// <c>.nfo</c> again (plan §3.1). All the text is untrusted.
/// </summary>
public sealed record StubMetadata
{
    /// <summary>Gets a value indicating whether the title is protected from fetchers: full metadata and locks.</summary>
    public required bool IsProtected { get; init; }

    /// <summary>Gets the title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the year.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the provider IDs, by the names Jellyfin uses.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

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

    /// <summary>Gets the status of a series.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the season of an episode.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the number of an episode.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>
    /// Builds the metadata of a catalog entry.
    /// </summary>
    /// <param name="item">The entry.</param>
    /// <param name="isProtected">Whether metadata protection is on.</param>
    /// <returns>The metadata.</returns>
    public static StubMetadata From(IndexedItem item, bool isProtected)
    {
        ArgumentNullException.ThrowIfNull(item);

        var core = new StubMetadata
        {
            IsProtected = isProtected,
            Title = item.Kind == IndexedItemKind.Movie ? FederatedPaths.WithoutYear(item.Name, item.ProductionYear) : item.Name,
            Year = item.ProductionYear,
            ProviderIds = item.ProviderIds
        };

        // Without protection the fetchers fill the rest (plan §3.1).
        return !isProtected ? core : core with
        {
            OriginalTitle = item.OriginalTitle,
            Overview = item.Overview,
            Taglines = item.Taglines,
            Genres = item.Genres,
            Studios = item.Studios,
            Tags = item.Tags,
            People = item.People,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating,
            PremiereDate = item.PremiereDate,
            EndDate = item.EndDate,
            Status = item.Status
        };
    }
}
