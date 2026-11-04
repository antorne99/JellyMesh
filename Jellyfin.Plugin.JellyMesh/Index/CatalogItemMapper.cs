using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Maps catalog entries to the items <see cref="DedupEngine"/> works on.
/// </summary>
public static class CatalogItemMapper
{
    private const string HdrRange = "HDR";

    /// <summary>
    /// Gets the films and episodes of a server. An episode takes title, year and IDs from its series, because Jellyfin
    /// sends none on episodes. If the series is not in the catalog it keeps only the series name, so it is not hidden.
    /// </summary>
    /// <param name="catalog">The catalog of the server.</param>
    /// <returns>The items.</returns>
    public static IReadOnlyList<CatalogItem> ToCatalogItems(ServerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var serverId = catalog.ServerId.ToString("N");
        var series = catalog.Items
            .Where(item => item.Kind == IndexedItemKind.Series)
            .DistinctBy(item => item.ItemId)
            .ToDictionary(item => item.ItemId, StringComparer.Ordinal);

        return catalog.Items
            .Where(item => item.Kind is IndexedItemKind.Movie or IndexedItemKind.Episode)
            .Select(item => ToCatalogItem(item, serverId, series))
            .ToList();
    }

    private static CatalogItem ToCatalogItem(IndexedItem item, string serverId, Dictionary<string, IndexedItem> series)
    {
        var versions = item.Versions.Select(ToMediaVersion).ToList();
        if (item.Kind == IndexedItemKind.Movie)
        {
            return new CatalogItem
            {
                ServerId = serverId,
                ItemId = item.ItemId,
                Kind = ContentKind.Movie,
                Title = item.Name,
                Year = item.ProductionYear,
                Ids = ContentIds.FromProviderIds(item.ProviderIds),
                Metadata = new ComparableMetadata
                {
                    OriginalTitle = item.OriginalTitle,
                    RunTimeTicks = item.RunTimeTicks,
                    Overview = item.Overview,
                    Genres = item.Genres,
                    Studios = item.Studios,
                    People = new Lazy<IReadOnlyList<string>>(item.People.Select(person => person.Name).OfType<string>().ToList())
                },
                Versions = versions
            };
        }

        var parent = item.SeriesId is { } seriesId ? series.GetValueOrDefault(seriesId) : null;
        return new CatalogItem
        {
            ServerId = serverId,
            ItemId = item.ItemId,
            Kind = ContentKind.Episode,
            Title = parent?.Name ?? item.SeriesName,
            Year = parent?.ProductionYear,
            Ids = ContentIds.FromProviderIds(parent?.ProviderIds),
            SeasonNumber = item.ParentIndexNumber,
            EpisodeNumber = item.IndexNumber,
            Versions = versions
        };
    }

    private static MediaVersion ToMediaVersion(IndexedVersion version)
    {
        var isHdr = string.Equals(version.VideoRange, HdrRange, StringComparison.OrdinalIgnoreCase);
        return new MediaVersion(version.MediaSourceId, new SourceQuality(version.Height, isHdr, version.BitRate, version.Codec));
    }
}
