namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The identity of a film or episode (plan §4.1).
/// </summary>
/// <param name="Key">
/// A string unique to this content within one <see cref="DedupResult"/>. It depends on the whole catalog: a new source
/// carrying a better ID can change it, so it is not a permanent name.
/// </param>
/// <param name="WorkKey">The film's key, or the key shared by all the episodes of the series.</param>
/// <param name="Kind">The kind of content.</param>
/// <param name="Match">How the identity was established.</param>
/// <param name="SeasonNumber">The season of an episode that has one.</param>
/// <param name="EpisodeNumber">The number of an episode that has one.</param>
public sealed record ContentIdentity(
    string Key,
    string WorkKey,
    ContentKind Kind,
    MatchKind Match,
    int? SeasonNumber = null,
    int? EpisodeNumber = null);
