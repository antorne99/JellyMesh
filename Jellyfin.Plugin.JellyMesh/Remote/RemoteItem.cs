using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A <c>BaseItemDto</c> of a remote catalog. Every value is untrusted text.
/// </summary>
/// <remarks>
/// This is a whitelist: only what is listed here is read from the answer. File paths and stream URLs, which can hold a
/// remote address or a token, are left out on purpose. <see cref="RemoteMediaSource.Path"/> is the one exception, and it
/// goes no further than the mapper.
/// </remarks>
public sealed record RemoteItem
{
    /// <summary>Gets the item ID on the remote.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the name.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the original title.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets the plot.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the taglines.</summary>
    public IReadOnlyList<string>? Taglines { get; init; }

    /// <summary>Gets the genres.</summary>
    public IReadOnlyList<string>? Genres { get; init; }

    /// <summary>Gets the studios.</summary>
    public IReadOnlyList<RemoteNamedItem>? Studios { get; init; }

    /// <summary>Gets the tags.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>Gets the cast and crew.</summary>
    public IReadOnlyList<RemotePerson>? People { get; init; }

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

    /// <summary>Gets the production year. Jellyfin does not send it on episodes.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the status of a series.</summary>
    public string? Status { get; init; }

    /// <summary>Gets all the provider IDs. Empty on episodes, in the test data.</summary>
    public IReadOnlyDictionary<string, string?>? ProviderIds { get; init; }

    /// <summary>Gets the duration.</summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>Gets when the item was added.</summary>
    public DateTimeOffset? DateCreated { get; init; }

    /// <summary>Gets the tag that changes with the item.</summary>
    public string? Etag { get; init; }

    /// <summary>Gets the season or episode number.</summary>
    public int? IndexNumber { get; init; }

    /// <summary>Gets the last episode of a file that holds several.</summary>
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
    public IReadOnlyDictionary<string, string>? ImageTags { get; init; }

    /// <summary>Gets the backdrop image tags.</summary>
    public IReadOnlyList<string>? BackdropImageTags { get; init; }

    /// <summary>Gets the chapters.</summary>
    public IReadOnlyList<RemoteChapter>? Chapters { get; init; }

    /// <summary>Gets the playable versions.</summary>
    public IReadOnlyList<RemoteMediaSource>? MediaSources { get; init; }
}
