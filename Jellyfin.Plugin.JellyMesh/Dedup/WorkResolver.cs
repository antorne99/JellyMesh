using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Groups items into works, a work being a film or a whole series, over one whole catalog. Grouping is done per work and
/// not per episode so that an episode found on one server still lands in the series known from the others.
/// </summary>
/// <remarks>
/// <para>
/// The plan does not say what to do when IDs only partly agree, so this follows the rule that a wrong match can hide a
/// title while a missed one only shows it twice (§9). Items with IDs are linked by any shared ID, directly or through
/// another item, unless the linked items disagree on the value of a provider: then every distinct set of IDs stays a work
/// of its own. Items with IDs are never linked to items without, nor by title and year, and a title with no year matches
/// nothing. Doubtful cases are reported as issues.
/// </para>
/// <para>The result is a property of the set of items, so it does not depend on their order.</para>
/// </remarks>
internal sealed class WorkResolver
{
    private const string MoviePrefix = "movie";
    private const string SeriesPrefix = "series";

    private readonly Dictionary<WorkIds, string> _keysByIds;

    private WorkResolver(Dictionary<WorkIds, string> keysByIds, List<DedupIssue> issues)
    {
        _keysByIds = keysByIds;
        Issues = issues;
    }

    /// <summary>
    /// Gets the doubtful matches found, sorted.
    /// </summary>
    public IReadOnlyList<DedupIssue> Issues { get; }

    /// <summary>
    /// Resolves the works of a catalog.
    /// </summary>
    /// <param name="items">Every item taking part, local and remote.</param>
    /// <returns>The resolver.</returns>
    public static WorkResolver Create(IReadOnlyCollection<CatalogItem> items)
    {
        var issues = new List<DedupIssue>();
        var signatures = items.Select(CanonicalIds).OfType<WorkIds>().Distinct().ToList();
        var resolver = new WorkResolver(LinkByIds(signatures, issues), issues);

        var possibleDuplicates = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .GroupBy(item => (item.Kind, Title: TitleNormalizer.Normalize(item.Title, item.Year), item.Year))
            .Select(group => group.Select(item => resolver.Find(item)?.Key).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList())
            .Where(keys => keys.Count > 1)
            .Select(keys => new DedupIssue(DedupIssueKind.PossibleDuplicate, keys));

        issues.AddRange(possibleDuplicates);
        issues.Sort(static (left, right) =>
        {
            var byKind = left.Kind.CompareTo(right.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(left.WorkKeys[0], right.WorkKeys[0]);
        });

        return resolver;
    }

    /// <summary>
    /// Finds the work of an item.
    /// </summary>
    /// <param name="item">The item, which must have been passed to <see cref="Create"/>.</param>
    /// <returns>The work, or <c>null</c> when the item has no IDs and lacks a title or a year.</returns>
    public Work? Find(CatalogItem item)
    {
        if (CanonicalIds(item) is { } ids)
        {
            return new Work(_keysByIds[ids], MatchKind.ProviderId);
        }

        if (string.IsNullOrWhiteSpace(item.Title) || item.Year is null)
        {
            return null;
        }

        var title = TitleNormalizer.Normalize(item.Title, item.Year);
        var key = string.Create(CultureInfo.InvariantCulture, $"{Prefix(item.Kind)}|title:{title}|{item.Year}");
        return new Work(key, MatchKind.TitleYear);
    }

    private static string Prefix(ContentKind kind) => kind == ContentKind.Movie ? MoviePrefix : SeriesPrefix;

    private static WorkIds? CanonicalIds(CatalogItem item)
    {
        // IDs of different kinds never match: TMDb numbers films and series separately.
        var tvdb = item.Kind == ContentKind.Movie ? null : Canonical(item.Ids.Tvdb);
        var ids = new ContentIds(Canonical(item.Ids.Tmdb), Canonical(item.Ids.Imdb), tvdb);
        return ids == ContentIds.None ? null : new WorkIds(item.Kind, ids);
    }

    private static string? Canonical(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();

    private static Dictionary<WorkIds, string> LinkByIds(List<WorkIds> signatures, List<DedupIssue> issues)
    {
        var groups = new DisjointSets(signatures.Count);
        var firstWithToken = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < signatures.Count; index++)
        {
            foreach (var token in Tokens(signatures[index]))
            {
                if (firstWithToken.TryGetValue(token, out var other))
                {
                    groups.Union(index, other);
                }
                else
                {
                    firstWithToken[token] = index;
                }
            }
        }

        var keys = new Dictionary<WorkIds, string>();
        foreach (var group in Enumerable.Range(0, signatures.Count).GroupBy(groups.Find))
        {
            var members = group.Select(index => signatures[index]).ToList();
            if (Agree(members))
            {
                // Every member gets the same key: the best ID the group has.
                var key = Tokens(new WorkIds(members[0].Kind, Combine(members))).First();
                members.ForEach(member => keys[member] = key);
            }
            else
            {
                members.ForEach(member => keys[member] = string.Join('|', [Prefix(member.Kind), .. IdParts(member.Ids)]));
                issues.Add(new DedupIssue(
                    DedupIssueKind.ConflictingProviderIds,
                    members.Select(member => keys[member]).Order(StringComparer.Ordinal).ToList()));
            }
        }

        return keys;
    }

    private static bool Agree(List<WorkIds> members) =>
        members.Select(member => member.Ids.Tmdb).OfType<string>().Distinct().Count() <= 1
        && members.Select(member => member.Ids.Imdb).OfType<string>().Distinct().Count() <= 1
        && members.Select(member => member.Ids.Tvdb).OfType<string>().Distinct().Count() <= 1;

    private static ContentIds Combine(List<WorkIds> members) => new(
        members.Select(member => member.Ids.Tmdb).FirstOrDefault(id => id is not null),
        members.Select(member => member.Ids.Imdb).FirstOrDefault(id => id is not null),
        members.Select(member => member.Ids.Tvdb).FirstOrDefault(id => id is not null));

    private static IEnumerable<string> Tokens(WorkIds work) =>
        IdParts(work.Ids).Select(part => string.Join('|', Prefix(work.Kind), part));

    private static IEnumerable<string> IdParts(ContentIds ids)
    {
        if (ids.Tmdb is not null)
        {
            yield return "tmdb:" + ids.Tmdb;
        }

        if (ids.Imdb is not null)
        {
            yield return "imdb:" + ids.Imdb;
        }

        if (ids.Tvdb is not null)
        {
            yield return "tvdb:" + ids.Tvdb;
        }
    }

    private readonly record struct WorkIds(ContentKind Kind, ContentIds Ids);

    /// <summary>
    /// The work of an item.
    /// </summary>
    /// <param name="Key">The work key.</param>
    /// <param name="Match">How it was established.</param>
    internal sealed record Work(string Key, MatchKind Match);

    private sealed class DisjointSets
    {
        private readonly int[] _parents;

        public DisjointSets(int count) => _parents = Enumerable.Range(0, count).ToArray();

        public int Find(int index)
        {
            while (_parents[index] != index)
            {
                _parents[index] = _parents[_parents[index]];
                index = _parents[index];
            }

            return index;
        }

        public void Union(int left, int right) => _parents[Find(left)] = Find(right);
    }
}
