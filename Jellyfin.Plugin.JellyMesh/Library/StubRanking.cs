using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Ranks the stubs of one title by the quality of their first source in the index (§4.3).
/// </summary>
public static class StubRanking
{
    /// <summary>
    /// Picks the best stub. A stub the index does not know ranks as one of unknown quality.
    /// </summary>
    /// <param name="stubIds">The stubs, at least one.</param>
    /// <param name="find">Finds a stub in the registry.</param>
    /// <param name="lookup">The items of the index.</param>
    /// <returns>The best stub.</returns>
    public static string Best(IReadOnlyList<string> stubIds, Func<string, StubEntry?> find, IndexItemLookup lookup)
    {
        return Order(stubIds, find, lookup)[0];
    }

    /// <summary>
    /// Ranks the stubs best first. A stub the index does not know ranks as one of unknown quality.
    /// </summary>
    /// <param name="stubIds">The stubs, at least one.</param>
    /// <param name="find">Finds a stub in the registry.</param>
    /// <param name="lookup">The items of the index.</param>
    /// <returns>The stubs, best first.</returns>
    public static IReadOnlyList<string> Order(IReadOnlyList<string> stubIds, Func<string, StubEntry?> find, IndexItemLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(stubIds);
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(lookup);

        var byStub = new List<(string StubId, RemoteSource Source)>();
        foreach (var stubId in stubIds)
        {
            var source = find(stubId)?.Sources is [var first, ..] ? first : null;
            var version = source is null
                ? null
                : lookup.Find(source.ServerId, source.ItemId)?.Versions.FirstOrDefault(v => string.Equals(v.MediaSourceId, source.MediaSourceId, StringComparison.Ordinal));
            var quality = version is null ? new SourceQuality(null, false, null, null) { IsRangeKnown = false } : CatalogItemMapper.QualityOf(version);

            // The stub ID stands for the item, so that two stubs never count as one source.
            byStub.Add((stubId, new RemoteSource(source?.ServerId.ToString("N") ?? string.Empty, stubId, source?.MediaSourceId ?? string.Empty, quality)));
        }

        return QualityRanking.Rank(byStub.Select(entry => entry.Source))
            .Select(ranked => byStub.First(entry => entry.Source == ranked).StubId)
            .ToList();
    }
}
