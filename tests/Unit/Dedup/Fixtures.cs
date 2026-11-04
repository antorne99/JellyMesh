using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Tests.Dedup;

/// <summary>
/// Builders for catalog data. Item and media source IDs come from the title alone, so the same title has the same IDs
/// on every server, as it really does in the test environment.
/// </summary>
internal static class Fixtures
{
    public const string Local = "locale";
    public const string Standard = "remoto-standard";
    public const string RemoteJellyMesh = "remoto-jellymesh";

    public static SourceQuality Quality(int height, int bitRate, string codec = "h264", bool hdr = false) =>
        new(height, hdr, bitRate, codec);

    public static RemoteSource Source(string server, SourceQuality quality, string id = "item") =>
        new(server, id, id, quality);

    public static CatalogItem Movie(
        string server,
        string title,
        int? year,
        ContentIds? ids,
        params SourceQuality[] versions) => new()
    {
        ServerId = server,
        ItemId = "item-" + title,
        Kind = ContentKind.Movie,
        Title = title,
        Year = year,
        Ids = ids ?? ContentIds.None,
        Versions = Versions("item-" + title, versions),
    };

    public static CatalogItem Episode(
        string server,
        string series,
        int? year,
        ContentIds? ids,
        int? season,
        int? episode,
        params SourceQuality[] versions)
    {
        var itemId = $"item-{series}-{season}x{episode}";
        return new CatalogItem
        {
            ServerId = server,
            ItemId = itemId,
            Kind = ContentKind.Episode,
            Title = series,
            Year = year,
            Ids = ids ?? ContentIds.None,
            SeasonNumber = season,
            EpisodeNumber = episode,
            Versions = Versions(itemId, versions),
        };
    }

    /// <summary>
    /// Gives a film a runtime, the one piece of metadata a file no fetcher recognised still has.
    /// </summary>
    public static CatalogItem Lasting(this CatalogItem item, int minutes) =>
        item with { Metadata = item.Metadata with { RunTimeTicks = minutes * TimeSpan.TicksPerMinute } };

    public static Lazy<IReadOnlyList<string>> People(params string[] names) => new(names);

    /// <summary>
    /// Gets a text form of a result covering everything in it, for comparing two results.
    /// </summary>
    public static string Fingerprint(DedupResult result)
    {
        var decisions = result.Decisions.Select(decision =>
            $"{decision.Identity} => {decision.Winner} / {string.Join(", ", decision.Backups)}");
        var issues = result.Issues.Select(issue => $"{issue.Kind}: {string.Join(", ", issue.WorkKeys)}");
        return string.Join('\n', decisions.Concat(issues));
    }

    /// <summary>
    /// Gets every ordering of a short list.
    /// </summary>
    public static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var others = items.Where((_, position) => position != index).ToList();
            foreach (var rest in Permutations(others))
            {
                yield return [items[index], .. rest];
            }
        }
    }

    public static List<T> Shuffled<T>(IEnumerable<T> items, Random random)
    {
        var list = items.ToList();
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
        return list;
    }

    private static MediaVersion[] Versions(string itemId, SourceQuality[] versions) =>
        versions.Select((quality, index) => new MediaVersion($"{itemId}-v{index}", quality)).ToArray();
}
