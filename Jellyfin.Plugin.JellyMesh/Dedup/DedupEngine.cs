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
    /// <param name="merges">The merges made by the administrator (decision 20), none if null.</param>
    /// <returns>The decisions and the doubtful matches.</returns>
    public static DedupResult Resolve(IEnumerable<CatalogItem> localItems, IEnumerable<CatalogItem> remoteItems, IReadOnlyList<ManualMerge>? merges = null)
    {
        ArgumentNullException.ThrowIfNull(localItems);
        ArgumentNullException.ThrowIfNull(remoteItems);

        var local = localItems.Where(item => !item.IsFederated).ToList();
        var remote = remoteItems.Where(item => !item.IsFederated && item.Versions.Count > 0).ToList();
        var works = WorkResolver.Create([.. local, .. remote], merges ?? []);

        var localByKey = local
            .SelectMany(item => Identify(item, works).Select(identity => (identity.Key, item.ItemId)))
            .ToLookup(entry => entry.Key, entry => entry.ItemId, StringComparer.Ordinal);

        var decisions = remote
            .SelectMany(item => IdentifyRemote(item, works).Select(identity => (Identity: identity, Item: item)))
            .GroupBy(entry => entry.Identity, entry => entry.Item)
            .Select(group => Decide(group.Key, group, localByKey[group.Key.Key].Distinct().Order(StringComparer.Ordinal).ToList()))
            .OrderBy(decision => decision.Identity.Key, StringComparer.Ordinal)
            .ToList();

        return new DedupResult(decisions, works.Issues)
        {
            LocalItemByWork = LocalItemsByWork(local, works),
            MergeCases = MergeCases(local, remote, works),
            Merges = works.Merges
        };
    }

    // Issues that share a title become one case, or a title would show twice and could be merged two ways. A case needs a
    // title with no local copy: two local films cannot become one, because a local item is never touched.
    private static List<MergeCase> MergeCases(List<CatalogItem> local, List<CatalogItem> remote, WorkResolver works)
    {
        var films = local.Select(item => new WorkCopy(item, true))
            .Concat(remote.Select(item => new WorkCopy(item, false)))
            .Where(copy => copy.Item.Kind == ContentKind.Movie)
            .Select(copy => (Copy: copy, works.Find(copy.Item)?.Key))
            .Where(film => film.Key is not null)
            .ToLookup(film => film.Key!, film => film.Copy, StringComparer.Ordinal);

        var cases = new List<(DedupIssueKind Kind, SortedSet<string> Keys)>();
        foreach (var issue in works.Issues.Where(issue => issue.WorkKeys.Count > 1 && issue.WorkKeys.All(films.Contains)))
        {
            var overlapping = cases.Where(entry => entry.Keys.Overlaps(issue.WorkKeys)).ToList();
            cases.RemoveAll(overlapping.Contains);

            // Issues are sorted by kind, so the earliest kind of the case is the one already there.
            var kind = overlapping.Count > 0 ? overlapping.Min(entry => entry.Kind) : issue.Kind;
            cases.Add((kind, new SortedSet<string>(overlapping.SelectMany(entry => entry.Keys).Concat(issue.WorkKeys), StringComparer.Ordinal)));
        }

        return cases
            .Select(entry => new MergeCase(entry.Kind, entry.Keys.Select(key => CaseWork(key, films[key], works)).ToList()))
            .Where(entry => entry.Works.Any(work => !work.HasLocalCopy))
            .OrderBy(entry => entry.Works[0].WorkKey, StringComparer.Ordinal)
            .ToList();
    }

    private static MergeCaseWork CaseWork(string key, IEnumerable<WorkCopy> copies, WorkResolver works)
    {
        var mergeIds = works.Merges
            .Where(merge => string.Equals(merge.WorkKey, key, StringComparison.Ordinal))
            .Select(merge => merge.Merge.Id)
            .ToList();
        var sorted = copies
            .DistinctBy(copy => (copy.Item.ServerId, copy.Item.ItemId))
            .OrderBy(copy => copy.Item.ServerId, StringComparer.Ordinal)
            .ThenBy(copy => copy.Item.ItemId, StringComparer.Ordinal)
            .ToList();
        return new MergeCaseWork(key, mergeIds, sorted);
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
        // Sorted, because StubMatcher tries them in turn, and the order of a read must not choose which stub stays.
        var remoteItems = items
            .Select(item => new RemoteItemKey(item.ServerId, item.ItemId))
            .OrderBy(item => item.ServerId, StringComparer.Ordinal)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .ToList();
        if (localItemIds.Count > 0)
        {
            return new FederationDecision(identity, null, []) { LocalItemIds = localItemIds, RemoteItems = remoteItems };
        }

        var ranked = QualityRanking.Rank(items.SelectMany(item => item.ToSources()));

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
