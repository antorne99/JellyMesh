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
    private readonly IManualMergeStore _merges;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationResolver"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="localCatalog">The local catalog.</param>
    /// <param name="registry">The server registry.</param>
    /// <param name="merges">The merges made by the administrator.</param>
    public FederationResolver(IFederationIndex index, ILocalCatalogReader localCatalog, IRemoteServerRegistry registry, IManualMergeStore merges)
    {
        _index = index;
        _localCatalog = localCatalog;
        _registry = registry;
        _merges = merges;
    }

    /// <inheritdoc />
    public FederationView Resolve() => Resolve(_index.Snapshot, _registry.GetServers());

    /// <inheritdoc />
    public FederationView Resolve(IndexSnapshot snapshot, IReadOnlyList<RemoteServerInfo> servers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(servers);

        // A server removed from the configuration can no longer serve anything, even if its entries are still stored.
        var names = servers.ToDictionary(server => server.Id, server => server.Name);
        var catalogs = snapshot.Servers.Where(catalog => names.ContainsKey(catalog.ServerId)).ToList();

        var local = _localCatalog.Read();
        var result = DedupEngine.Resolve(local, catalogs.SelectMany(CatalogItemMapper.ToCatalogItems), _merges.Merges);

        var details = catalogs
            .SelectMany(catalog => catalog.Items.Select(item => (Catalog: catalog, Item: item)))
            .ToLookup(entry => (Server: entry.Catalog.ServerId.ToString("N"), entry.Item.ItemId));

        // Server IDs are parsed, not looked up by text: a removed server just has no name.
        string? NameOf(MergedCopy copy) =>
            !copy.IsLocal && Guid.TryParse(copy.ServerId, out var server) ? names.GetValueOrDefault(server) : null;

        MergeCopyView ToCopyView(WorkCopy copy)
        {
            var merged = MergedCopy.From(copy.Item, copy.IsLocal);
            return new MergeCopyView(merged, NameOf(merged), FilmOnly(copy.Item)?.Metadata.RunTimeTicks, true) { Versions = BestFirst(FilmOnly(copy.Item)) };
        }

        MergeCopyView ToMergedCopyView(AppliedCopy copy) =>
            new(copy.Copy, NameOf(copy.Copy), FilmOnly(copy.Item)?.Metadata.RunTimeTicks, copy.Item is not null) { Versions = BestFirst(FilmOnly(copy.Item)) };

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
            LocalItemByWork = result.LocalItemByWork.ToDictionary(entry => entry.Key, entry => ToLocalId(entry.Value), StringComparer.Ordinal),
            MergeCases = result.MergeCases
                .Select(entry => new MergeCaseView(
                    entry.Kind,
                    entry.Works.Select(work => new MergeTitleView(work.WorkKey, work.HasLocalCopy, work.MergeIds, work.Copies.Select(ToCopyView).ToList())).ToList())
                {
                    ContentKind = entry.ContentKind
                })
                .ToList(),
            ManualMerges = result.Merges
                .Select(merge => new ManualMergeView(merge.Merge.Id, merge.Merge.CreatedUtc, merge.IsInEffect, merge.Copies.Select(ToMergedCopyView).ToList()))
                .ToList()
        };
    }

    private static List<MediaVersion> BestFirst(CatalogItem? item)
    {
        return item is null
            ? []
            : QualityRanking.Rank(item.ToSources()).Select(source => new MediaVersion(source.MediaSourceId, source.Quality)).ToList();
    }

    // A series copy is shown by one of its episodes, whose duration and versions say nothing of the series.
    private static CatalogItem? FilmOnly(CatalogItem? item) => item?.Kind == ContentKind.Movie ? item : null;

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
