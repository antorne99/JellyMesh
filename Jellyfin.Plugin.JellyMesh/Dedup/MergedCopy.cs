using System;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// One film copy of a <see cref="ManualMerge"/>, named so that it can be found again. A work key is not a permanent name,
/// so a copy is kept by its server and item, and by its IDs as they were.
/// </summary>
/// <remarks>
/// An item is this copy if it is the same item on the same server, whatever its IDs say now, so a copy that gains or
/// changes IDs stays in the merge. Otherwise it must have exactly these IDs, which covers a file that moved or a server
/// added again. One shared ID is not enough here: in the chain A(1, a), B(1, b), C(2, b), merging A with B must leave C out.
/// </remarks>
public sealed record MergedCopy
{
    /// <summary>Gets the server, as in <see cref="CatalogItem.ServerId"/>.</summary>
    public required string ServerId { get; init; }

    /// <summary>Gets the item on that server.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets a value indicating whether this is a copy in the local library.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Gets the TMDb ID the copy had, in canonical form.</summary>
    public string? Tmdb { get; init; }

    /// <summary>Gets the IMDb ID the copy had, in canonical form.</summary>
    public string? Imdb { get; init; }

    /// <summary>Gets the title the copy had, to show it while it cannot be found.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the year the copy had.</summary>
    public int? Year { get; init; }

    /// <summary>
    /// Names a copy.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="isLocal">Whether it is in the local library.</param>
    /// <returns>The copy.</returns>
    public static MergedCopy From(CatalogItem item, bool isLocal)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new MergedCopy
        {
            ServerId = item.ServerId,
            ItemId = item.ItemId,
            IsLocal = isLocal,
            Tmdb = WorkResolver.Canonical(item.Ids.Tmdb),
            Imdb = WorkResolver.Canonical(item.Ids.Imdb),
            Title = item.Title,
            Year = item.Year
        };
    }
}
