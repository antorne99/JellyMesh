namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The kinds of doubtful match reported by the engine.
/// </summary>
public enum DedupIssueKind
{
    /// <summary>
    /// Linked items disagree on the value of a provider. With one key, they were merged all the same, because one shared ID
    /// is enough (decision 13). With several, a chain of shared IDs joined two copies with nothing in common, and every
    /// distinct set of IDs stays separate, so that a wrong match cannot hide a title (plan §9), until the administrator
    /// merges them by hand (decision 20).
    /// </summary>
    ConflictingProviderIds,

    /// <summary>
    /// Items have the same normalized title and year but neither IDs nor metadata link them, so they were not merged and
    /// may show twice. Films can be merged by hand (decision 20).
    /// </summary>
    PossibleDuplicate,
}
