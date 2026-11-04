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

        var localKeys = local
            .Select(item => Identify(item, works)?.Key)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var decisions = remote
            .GroupBy(item => Identify(item, works) ?? IdentifyAlone(item, works))
            .Select(group => Decide(group.Key, group, localKeys.Contains(group.Key.Key)))
            .OrderBy(decision => decision.Identity.Key, StringComparer.Ordinal)
            .ToList();

        return new DedupResult(decisions, works.Issues);
    }

    private static FederationDecision Decide(ContentIdentity identity, IEnumerable<CatalogItem> items, bool isLocal)
    {
        if (isLocal)
        {
            return new FederationDecision(identity, null, []);
        }

        var ranked = QualityRanking.Rank(items.SelectMany(item => item.Versions.Select(version =>
            new RemoteSource(item.ServerId, item.ItemId, version.MediaSourceId, version.Quality))));

        return new FederationDecision(identity, ranked[0], ranked.Skip(1).ToList());
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
