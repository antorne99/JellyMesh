using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Applies the deduplication rules of plan §4 to a local and a remote catalog. Pure and stateless.
/// </summary>
public static class DedupEngine
{
    /// <summary>
    /// Works out what to materialize. The result depends only on the items passed in, not on their order.
    /// </summary>
    /// <param name="localItems">
    /// The real local copies: films and episodes with a file, with no virtual or missing items. Federated stubs are
    /// ignored when flagged with <see cref="CatalogItem.IsFederated"/>.
    /// </param>
    /// <param name="remoteItems">The items of every remote server. Those flagged as federated are never imported.</param>
    /// <returns>The decisions and the doubtful matches.</returns>
    public static DedupResult Resolve(IEnumerable<CatalogItem> localItems, IEnumerable<CatalogItem> remoteItems)
    {
        ArgumentNullException.ThrowIfNull(localItems);
        ArgumentNullException.ThrowIfNull(remoteItems);

        var local = localItems.Where(item => !item.IsFederated).ToList();
        var remote = remoteItems.Where(item => !item.IsFederated && item.Versions.Count > 0).ToList();
        var works = WorkResolver.Create([.. local, .. remote]);

        var localByKey = local
            .Select(item => (Identify(item, works)?.Key, item.ItemId))
            .Where(entry => entry.Key is not null)
            .ToLookup(entry => entry.Key!, entry => entry.ItemId, StringComparer.Ordinal);

        var decisions = remote
            .GroupBy(item => Identify(item, works) ?? IdentifyAlone(item, works))
            .Select(group => Decide(group.Key, group, localByKey[group.Key.Key].Order(StringComparer.Ordinal).ToList()))
            .OrderBy(decision => decision.Identity.Key, StringComparer.Ordinal)
            .ToList();

        return new DedupResult(decisions, works.Issues) { LocalItemByWork = LocalItemsByWork(local, works) };
    }

    private static Dictionary<string, string> LocalItemsByWork(List<CatalogItem> local, WorkResolver works)
    {
        return local
            .Select(item => (Key: works.Find(item)?.Key, item.ItemId))
            .Where(entry => entry.Key is not null)
            .GroupBy(entry => entry.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.ItemId).Min(StringComparer.Ordinal)!, StringComparer.Ordinal);
    }

    private static FederationDecision Decide(ContentIdentity identity, IGrouping<ContentIdentity, CatalogItem> items, List<string> localItemIds)
    {
        var remoteItems = items.Select(item => new RemoteItemKey(item.ServerId, item.ItemId)).ToList();
        if (localItemIds.Count > 0)
        {
            return new FederationDecision(identity, null, []) { LocalItemIds = localItemIds, RemoteItems = remoteItems };
        }

        var ranked = QualityRanking.Rank(items.SelectMany(item => item.Versions.Select(version =>
            new RemoteSource(item.ServerId, item.ItemId, version.MediaSourceId, version.Quality))));

        return new FederationDecision(identity, ranked[0], ranked.Skip(1).ToList()) { RemoteItems = remoteItems };
    }

    private static ContentIdentity? Identify(CatalogItem item, WorkResolver works)
    {
        if (works.Find(item) is not { } work)
        {
            return null;
        }

        if (item.Kind == ContentKind.Movie)
        {
            return new ContentIdentity(work.Key, work.Key, ContentKind.Movie, work.Match);
        }

        if (item.SeasonNumber is not { } season || item.EpisodeNumber is not { } episode)
        {
            return null;
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{work.Key}|S{season}E{episode}");
        return new ContentIdentity(key, work.Key, ContentKind.Episode, work.Match, season, episode);
    }

    private static ContentIdentity IdentifyAlone(CatalogItem item, WorkResolver works)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{item.Kind}|item:{item.ServerId}:{item.ItemId}");
        return new ContentIdentity(
            key,
            works.Find(item)?.Key ?? key,
            item.Kind,
            MatchKind.ItemOnly,
            item.SeasonNumber,
            item.EpisodeNumber);
    }
}
