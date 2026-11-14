using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// One film or series copy of a <see cref="ManualMerge"/>, named so that it can be found again. A work key is not a
/// permanent name, so a copy is kept by its server and item, and by its IDs as they were. A series is named by its series
/// item, never by an episode (decision 51).
/// </summary>
/// <remarks>
/// An item is this copy if it is the same item on the same server, whatever its IDs say now, so a copy that gains or
/// changes IDs stays in the merge. Otherwise it must have exactly these IDs, which covers a file that moved or a server
/// added again. One shared ID is not enough here: in the chain A(1, a), B(1, b), C(2, b), merging A with B must leave C out.
/// A copy only finds items of its own kind, since TMDb numbers films and series apart.
/// </remarks>
public sealed record MergedCopy
{
    /// <summary>Gets the server, as in <see cref="CatalogItem.ServerId"/>.</summary>
    public required string ServerId { get; init; }

    /// <summary>Gets the item on that server: the film, or the series of a series copy.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets whether this is a film or a series, whose items are episodes. A file written before series is films.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ContentKind>))]
    public ContentKind Kind { get; init; }

    /// <summary>Gets a value indicating whether this is a copy in the local library.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Gets the TMDb ID the copy had, in canonical form.</summary>
    public string? Tmdb { get; init; }

    /// <summary>Gets the IMDb ID the copy had, in canonical form.</summary>
    public string? Imdb { get; init; }

    /// <summary>Gets the TVDB ID a series copy had, in canonical form. Films do not use it.</summary>
    public string? Tvdb { get; init; }

    /// <summary>Gets the title the copy had, to show it while it cannot be found.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the year the copy had.</summary>
    public int? Year { get; init; }

    /// <summary>
    /// Names a copy: a film by its item, a series by the series of its episode.
    /// </summary>
    /// <param name="item">The film, or an episode of the series.</param>
    /// <param name="isLocal">Whether it is in the local library.</param>
    /// <returns>The copy.</returns>
    public static MergedCopy From(CatalogItem item, bool isLocal)
    {
        ArgumentNullException.ThrowIfNull(item);

        var isFilm = item.Kind == ContentKind.Movie;
        return new MergedCopy
        {
            ServerId = item.ServerId,
            ItemId = isFilm ? item.ItemId : item.SeriesId ?? item.ItemId,
            Kind = item.Kind,
            IsLocal = isLocal,
            Tmdb = WorkResolver.Canonical(item.Ids.Tmdb),
            Imdb = WorkResolver.Canonical(item.Ids.Imdb),
            Tvdb = isFilm ? null : WorkResolver.Canonical(item.Ids.Tvdb),
            Title = item.Title,
            Year = item.Year
        };
    }
}
