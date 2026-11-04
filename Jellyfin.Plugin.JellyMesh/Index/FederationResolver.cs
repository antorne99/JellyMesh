using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// <see cref="IFederationResolver"/> over the index, the local library and <see cref="DedupEngine"/>.
/// </summary>
public sealed class FederationResolver : IFederationResolver
{
    private readonly IFederationIndex _index;
    private readonly ILocalCatalogReader _localCatalog;
    private readonly IRemoteServerRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationResolver"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="localCatalog">The local catalog.</param>
    /// <param name="registry">The server registry.</param>
    public FederationResolver(IFederationIndex index, ILocalCatalogReader localCatalog, IRemoteServerRegistry registry)
    {
        _index = index;
        _localCatalog = localCatalog;
        _registry = registry;
    }

    /// <inheritdoc />
    public FederationView Resolve()
    {
        var snapshot = _index.Snapshot;

        // A server removed from the configuration can no longer serve anything, even if its entries are still stored.
        var names = _registry.GetServers().ToDictionary(server => server.Id, server => server.Name);
        var catalogs = snapshot.Servers.Where(catalog => names.ContainsKey(catalog.ServerId)).ToList();

        var local = _localCatalog.Read();
        var result = DedupEngine.Resolve(local, catalogs.SelectMany(CatalogItemMapper.ToCatalogItems));

        var details = catalogs
            .SelectMany(catalog => catalog.Items.Select(item => (Catalog: catalog, Item: item)))
            .ToLookup(entry => (Server: entry.Catalog.ServerId.ToString("N"), entry.Item.ItemId));

        ResolvedSource ToSource(RemoteSource source)
        {
            var (catalog, item) = details[(source.ServerId, source.ItemId)].First();
            return new ResolvedSource(
                catalog.ServerId,
                names[catalog.ServerId],
                catalog.State,
                source.ItemId,
                source.MediaSourceId,
                TitleOf(item),
                source.Quality,
                item.LastSeenUtc);
        }

        var contents = result.Decisions
            .Select(decision => new ResolvedContent(
                decision.Identity,
                decision.IsLocal,
                decision.Winner is { } winner ? ToSource(winner) : null,
                decision.Backups.Select(ToSource).ToList())
            {
                LocalItemIds = decision.LocalItemIds.Select(ToLocalId).ToList(),
                RemoteItems = decision.RemoteItems.Select(ToResolvedItem).ToList()
            })
            .ToList();

        return new FederationView(snapshot.Health, local.Count, contents, result.Issues)
        {
            LocalItemByWork = result.LocalItemByWork.ToDictionary(entry => entry.Key, entry => ToLocalId(entry.Value), StringComparer.Ordinal)
        };
    }

    private static Guid ToLocalId(string itemId) => Guid.TryParse(itemId, out var id) ? id : Guid.Empty;

    private static ResolvedItem ToResolvedItem(RemoteItemKey item) => new(Guid.Parse(item.ServerId), item.ItemId);

    private static string? TitleOf(IndexedItem item)
    {
        if (item.Kind != IndexedItemKind.Episode)
        {
            return item.Name;
        }

        var end = item.IndexNumberEnd is { } last ? string.Create(CultureInfo.InvariantCulture, $"-E{last}") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{item.SeriesName} S{item.ParentIndexNumber}E{item.IndexNumber}{end}");
    }
}
