using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A film or episode in a catalog, local or remote, as mapped from <c>BaseItemDto</c>.
/// </summary>
/// <remarks>
/// For an episode, <see cref="Title"/>, <see cref="Year"/> and <see cref="Ids"/> describe the parent series. Jellyfin
/// returns empty <c>ProviderIds</c> and no <c>ProductionYear</c> on episodes, so the mapper has to read them from the
/// Series item.
/// </remarks>
public sealed record CatalogItem
{
    /// <summary>
    /// Gets the plugin's ID for the server holding the item. Item IDs are not unique across servers, so this is part of
    /// a source's identity. Not read for local items.
    /// </summary>
    public required string ServerId { get; init; }

    /// <summary>
    /// Gets the item ID on that server.
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Gets the kind of item.
    /// </summary>
    public required ContentKind Kind { get; init; }

    /// <summary>
    /// Gets the film's <c>Name</c>, or the series' <c>Name</c> for an episode. A trailing <c>(year)</c> may remain, since
    /// Jellyfin leaves it there without a <c>.nfo</c>, and the normalization deals with it.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Gets the film's <c>ProductionYear</c>, or the series' for an episode.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Gets the film's provider IDs, or the series' for an episode.
    /// </summary>
    public ContentIds Ids { get; init; } = ContentIds.None;

    /// <summary>
    /// Gets what confirms a film copy that has no IDs (decision 13). Filled on films only.
    /// </summary>
    public ComparableMetadata Metadata { get; init; } = ComparableMetadata.None;

    /// <summary>
    /// Gets the season of an episode, <c>ParentIndexNumber</c>.
    /// </summary>
    public int? SeasonNumber { get; init; }

    /// <summary>
    /// Gets the number of an episode, <c>IndexNumber</c>.
    /// </summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>
    /// Gets a value indicating whether the item is itself federated on its server (plan §4.4). Such an item is ignored
    /// in both catalogs: a remote one is never imported, and a local one is JellyMesh's own stub, not a local copy.
    /// </summary>
    public bool IsFederated { get; init; }

    /// <summary>
    /// Gets the playable versions. Only read for remote items, and a remote item without any is skipped.
    /// </summary>
    public IReadOnlyList<MediaVersion> Versions { get; init; } = [];
}
