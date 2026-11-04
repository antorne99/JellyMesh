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
    // The numbers come from a remote: a file claiming more than this is taken for a single episode.
    private const int MaxEpisodesInOneFile = 50;

    /// <summary>
    /// Works out what to materialize. The result depends only on the items passed in, not on their order.
    /// </summary>
    /// <remarks>
    /// A file with several episodes counts once for each of them (decision 13a), so a local one suppresses the remote
    /// copies of all its episodes, and a remote one competes for each episode on its own.
    /// </remarks>
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
            .SelectMany(item => Identify(item, works).Select(identity => (identity.Key, item.ItemId)))
            .ToLookup(entry => entry.Key, entry => entry.ItemId, StringComparer.Ordinal);

        var decisions = remote
            .SelectMany(item => IdentifyRemote(item, works).Select(identity => (Identity: identity, Item: item)))
            .GroupBy(entry => entry.Identity, entry => entry.Item)
            .Select(group => Decide(group.Key, group, localByKey[group.Key.Key].Distinct().Order(StringComparer.Ordinal).ToList()))
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

    private static List<ContentIdentity> IdentifyRemote(CatalogItem item, WorkResolver works)
    {
        var identities = Identify(item, works);
        return identities.Count > 0 ? identities : IdentifyAlone(item, works);
    }

    private static List<ContentIdentity> Identify(CatalogItem item, WorkResolver works)
    {
        if (works.Find(item) is not { } work)
        {
            return [];
        }

        if (item.Kind == ContentKind.Movie)
        {
            return [new ContentIdentity(work.Key, work.Key, ContentKind.Movie, work.Match)];
        }

        if (item.SeasonNumber is not { } season)
        {
            return [];
        }

        return EpisodesOf(item)
            .Select(episode => new ContentIdentity(
                string.Create(CultureInfo.InvariantCulture, $"{work.Key}|S{season}E{episode}"),
                work.Key,
                ContentKind.Episode,
                work.Match,
                season,
                episode))
            .ToList();
    }

    private static List<ContentIdentity> IdentifyAlone(CatalogItem item, WorkResolver works)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{item.Kind}|item:{item.ServerId}:{item.ItemId}");
        var workKey = works.Find(item)?.Key ?? key;
        var episodes = EpisodesOf(item);
        if (episodes.Count <= 1)
        {
            return [new ContentIdentity(key, workKey, item.Kind, MatchKind.ItemOnly, item.SeasonNumber, item.EpisodeNumber)];
        }

        return episodes
            .Select(episode => new ContentIdentity(
                string.Create(CultureInfo.InvariantCulture, $"{key}|E{episode}"),
                workKey,
                item.Kind,
                MatchKind.ItemOnly,
                item.SeasonNumber,
                episode))
            .ToList();
    }

    private static List<int> EpisodesOf(CatalogItem item)
    {
        if (item.EpisodeNumber is not { } first)
        {
            return [];
        }

        // Without a season the numbers have no place in a series anyway.
        return item.SeasonNumber is not null && item.EpisodeNumberEnd is { } last && last > first && last - first < MaxEpisodesInOneFile
            ? Enumerable.Range(first, last - first + 1).ToList()
            : [first];
    }
}
