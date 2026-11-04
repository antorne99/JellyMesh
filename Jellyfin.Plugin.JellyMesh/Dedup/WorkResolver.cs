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
/// IDs (decision 13). One shared ID is enough to merge, even when the other IDs differ, and sharing is transitive: a third
/// copy holding both IDs joins two copies that have none in common. The exception is a chain that joins two copies which
/// contradict each other: they share no ID and hold different values for the same provider. At least one ID in that chain
/// is wrong and nobody can tell which, so every distinct set of IDs in it stays a work of its own and the case is reported.
/// A false merge hides a title, while a duplicate only shows it twice.
/// </para>
/// <para>
/// Film copies without IDs (decision 13). Title and year only propose a match, and <see cref="MetadataComparison"/> decides
/// it, against other copies without IDs and against copies with IDs. Within one title and year, a set of copies connected by
/// confirmations merges only if every pair in it confirms and it holds at most one film with IDs. Anything else, such as a
/// copy that could be either of two films, leaves all those copies on their own. A film without IDs and without a year or a
/// title matches nothing.
/// </para>
/// <para>Series without IDs keep §4.1: normalized title and year, and never a link to a series with IDs.</para>
/// <para>The result is a property of the set of items, so it does not depend on their order.</para>
/// </remarks>
internal sealed class WorkResolver
{
    private const string MoviePrefix = "movie";
    private const string SeriesPrefix = "series";

    private readonly Dictionary<WorkIds, string> _keysByIds;
    private readonly Dictionary<(string ServerId, string ItemId), Work> _filmsWithoutIds;

    private WorkResolver(Dictionary<WorkIds, string> keysByIds, List<DedupIssue> issues)
    {
        _keysByIds = keysByIds;
        _filmsWithoutIds = [];
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
        resolver.LinkFilmsWithoutIds(items);

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
            return byKind != 0 ? byKind : string.CompareOrdinal(string.Join('\n', left.WorkKeys), string.Join('\n', right.WorkKeys));
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

        if (!HasTitleAndYear(item))
        {
            return null;
        }

        return item.Kind == ContentKind.Movie
            ? _filmsWithoutIds[(item.ServerId, item.ItemId)]
            : new Work(TitleKey(item), MatchKind.TitleYear);
    }

    private static string Prefix(ContentKind kind) => kind == ContentKind.Movie ? MoviePrefix : SeriesPrefix;

    private static bool HasTitleAndYear(CatalogItem item) => !string.IsNullOrWhiteSpace(item.Title) && item.Year is not null;

    private static string TitleKey(CatalogItem item)
    {
        var title = TitleNormalizer.Normalize(item.Title, item.Year);
        return string.Create(CultureInfo.InvariantCulture, $"{Prefix(item.Kind)}|title:{title}|{item.Year}");
    }

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
            if (HasContradiction(members))
            {
                members.ForEach(member => keys[member] = string.Join('|', [Prefix(member.Kind), .. IdParts(member.Ids)]));
                issues.Add(new DedupIssue(
                    DedupIssueKind.ConflictingProviderIds,
                    members.Select(member => keys[member]).Order(StringComparer.Ordinal).ToList()));
                continue;
            }

            // Every member gets the same key: the first provider the group has, and its lowest value, because the order
            // of the items must not choose. No other group can hold that ID, or it would be in this one.
            var key = GroupKey(members);
            members.ForEach(member => keys[member] = key);
            if (!Agree(members))
            {
                issues.Add(new DedupIssue(DedupIssueKind.ConflictingProviderIds, [key]));
            }
        }

        return keys;
    }

    private static bool HasContradiction(List<WorkIds> members)
    {
        return members.Exists(left => members.Exists(right => Contradict(left.Ids, right.Ids)));
    }

    private static bool Contradict(ContentIds left, ContentIds right)
    {
        bool Same(string? first, string? second) => first is not null && first == second;
        bool Differ(string? first, string? second) => first is not null && second is not null && first != second;

        var shareAnId = Same(left.Tmdb, right.Tmdb) || Same(left.Imdb, right.Imdb) || Same(left.Tvdb, right.Tvdb);
        return !shareAnId && (Differ(left.Tmdb, right.Tmdb) || Differ(left.Imdb, right.Imdb) || Differ(left.Tvdb, right.Tvdb));
    }

    private static bool Agree(List<WorkIds> members) =>
        members.Select(member => member.Ids.Tmdb).OfType<string>().Distinct().Count() <= 1
        && members.Select(member => member.Ids.Imdb).OfType<string>().Distinct().Count() <= 1
        && members.Select(member => member.Ids.Tvdb).OfType<string>().Distinct().Count() <= 1;

    private static string GroupKey(List<WorkIds> members)
    {
        static string? Lowest(IEnumerable<string?> ids) => ids.OfType<string>().Order(StringComparer.Ordinal).FirstOrDefault();

        var ids = new ContentIds(
            Lowest(members.Select(member => member.Ids.Tmdb)),
            Lowest(members.Select(member => member.Ids.Imdb)),
            Lowest(members.Select(member => member.Ids.Tvdb)));
        return Tokens(new WorkIds(members[0].Kind, ids)).First();
    }

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

    private static string CopyName(CatalogItem item) => item.ServerId + ":" + item.ItemId;

    // Copies of the work under other titles are not looked at: they may well be in another language.
    private static bool ConfirmsWork(CatalogItem copy, IEnumerable<CatalogItem> work)
    {
        var verdicts = work.Select(member => MetadataComparison.Compare(copy.Metadata, member.Metadata)).ToList();
        return !verdicts.Contains(MetadataVerdict.Contradicted) && verdicts.Contains(MetadataVerdict.Confirmed);
    }

    private static bool EveryPairConfirmed(List<int> copies, List<int> works, HashSet<(int Low, int High)> confirmed)
    {
        var copyPairs = copies.SelectMany((low, index) => copies.Skip(index + 1).Select(high => (low, high)));
        var workPairs = copies.SelectMany(copy => works.Select(work => (copy, work)));
        return copyPairs.Concat(workPairs).All(confirmed.Contains);
    }

    private void LinkFilmsWithoutIds(IReadOnlyCollection<CatalogItem> items)
    {
        var films = items.Where(item => item.Kind == ContentKind.Movie && HasTitleAndYear(item));
        foreach (var candidates in films.GroupBy(TitleKey, StringComparer.Ordinal))
        {
            var withIds = candidates.Where(item => CanonicalIds(item) is not null).ToList();
            var copies = candidates
                .Where(item => CanonicalIds(item) is null)
                .DistinctBy(item => (item.ServerId, item.ItemId))
                .OrderBy(CopyName, StringComparer.Ordinal)
                .ToList();
            if (copies.Count > 0)
            {
                LinkCandidates(candidates.Key, copies, withIds);
            }
        }
    }

    private void LinkCandidates(string titleKey, List<CatalogItem> copies, List<CatalogItem> withIds)
    {
        // Positions below copies.Count are copies, the others are works.
        var works = withIds
            .GroupBy(item => _keysByIds[CanonicalIds(item)!.Value], StringComparer.Ordinal)
            .OrderBy(work => work.Key, StringComparer.Ordinal)
            .ToList();
        var confirmed = new HashSet<(int Low, int High)>();
        var groups = new DisjointSets(copies.Count + works.Count);

        void Confirm(int low, int high)
        {
            confirmed.Add((low, high));
            groups.Union(low, high);
        }

        for (var copy = 0; copy < copies.Count; copy++)
        {
            for (var other = copy + 1; other < copies.Count; other++)
            {
                if (MetadataComparison.Compare(copies[copy].Metadata, copies[other].Metadata) == MetadataVerdict.Confirmed)
                {
                    Confirm(copy, other);
                }
            }

            for (var work = 0; work < works.Count; work++)
            {
                if (ConfirmsWork(copies[copy], works[work]))
                {
                    Confirm(copy, copies.Count + work);
                }
            }
        }

        // Each entry is a set of copies that stay together, with the key of the film with IDs they joined, if any.
        var outcome = new List<(List<CatalogItem> Copies, string? WorkKey)>();
        foreach (var group in Enumerable.Range(0, copies.Count + works.Count).GroupBy(groups.Find))
        {
            var copyMembers = group.Where(member => member < copies.Count).Order().ToList();
            var workMembers = group.Where(member => member >= copies.Count).ToList();
            if (copyMembers.Count == 0)
            {
                continue;
            }

            var members = copyMembers.Select(member => copies[member]).ToList();
            if (workMembers.Count <= 1 && EveryPairConfirmed(copyMembers, workMembers, confirmed))
            {
                outcome.Add((members, workMembers.Count == 1 ? works[workMembers[0] - copies.Count].Key : null));
            }
            else
            {
                outcome.AddRange(members.Select(copy => (new List<CatalogItem> { copy }, (string?)null)));
            }
        }

        // The plain title key whenever it is unambiguous, so that the key of a film alone does not depend on its item ID.
        var titleGroups = outcome.Count(entry => entry.WorkKey is null);
        foreach (var (members, workKey) in outcome)
        {
            var work = workKey is not null
                ? new Work(workKey, MatchKind.ProviderId)
                : new Work(titleGroups == 1 ? titleKey : titleKey + "|" + CopyName(members[0]), MatchKind.TitleYear);
            members.ForEach(copy => _filmsWithoutIds[(copy.ServerId, copy.ItemId)] = work);
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
