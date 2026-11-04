namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The kinds of doubtful match reported by the engine. In both, the titles involved stay separate, so that a wrong match
/// cannot hide one (plan §9).
/// </summary>
public enum DedupIssueKind
{
    /// <summary>
    /// Items share a provider ID but disagree on the value of another provider, for example the same TMDb ID with two
    /// different IMDb IDs.
    /// </summary>
    ConflictingProviderIds,

    /// <summary>
    /// Items have the same normalized title and year but no ID links them, so they were not merged and may show twice.
    /// </summary>
    PossibleDuplicate,
}
