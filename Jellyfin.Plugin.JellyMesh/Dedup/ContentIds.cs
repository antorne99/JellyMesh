using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The provider IDs used for identity (plan §4.1): the film's, or the parent series' for an episode.
/// </summary>
/// <param name="Tmdb">The TMDb ID, or <c>null</c> when absent.</param>
/// <param name="Imdb">The IMDb ID, or <c>null</c> when absent.</param>
/// <param name="Tvdb">The TVDB ID, or <c>null</c> when absent. Ignored on films: the plan does not list it for them.</param>
public sealed record ContentIds(string? Tmdb = null, string? Imdb = null, string? Tvdb = null)
{
    /// <summary>
    /// Gets an instance with no IDs.
    /// </summary>
    public static ContentIds None { get; } = new();

    /// <summary>
    /// Reads the IDs from a Jellyfin <c>ProviderIds</c> dictionary, whose keys are <c>Tmdb</c>, <c>Imdb</c>, <c>Tvdb</c>.
    /// Key case is ignored and other providers are skipped.
    /// </summary>
    /// <param name="providerIds">The dictionary, or <c>null</c> when the item has none.</param>
    /// <returns>The IDs found.</returns>
    public static ContentIds FromProviderIds(IReadOnlyDictionary<string, string>? providerIds)
    {
        if (providerIds is null)
        {
            return None;
        }

        string? tmdb = null, imdb = null, tvdb = null;
        foreach (var (provider, value) in providerIds)
        {
            if (provider.Equals(nameof(Tmdb), StringComparison.OrdinalIgnoreCase))
            {
                tmdb = value;
            }
            else if (provider.Equals(nameof(Imdb), StringComparison.OrdinalIgnoreCase))
            {
                imdb = value;
            }
            else if (provider.Equals(nameof(Tvdb), StringComparison.OrdinalIgnoreCase))
            {
                tvdb = value;
            }
        }

        return new ContentIds(tmdb, imdb, tvdb);
    }
}
