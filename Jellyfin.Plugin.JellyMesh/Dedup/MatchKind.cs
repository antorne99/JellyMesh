namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// How a content identity was established (plan §4.1).
/// </summary>
public enum MatchKind
{
    /// <summary>
    /// By provider ID: of the film, or of the parent series for an episode. A film copy without IDs that the metadata tied
    /// to such a film gets this too.
    /// </summary>
    ProviderId,

    /// <summary>
    /// By normalized title and year. For an episode that is enough, on the parent series. For a film the metadata must
    /// confirm it (decision 13), and a film they tie to no other copy is on its own.
    /// </summary>
    TitleYear,

    /// <summary>
    /// Nothing to match on: no ID and no title or no year, or an episode without season or episode number.
    /// The item stands alone and is imported, so that it is not hidden.
    /// </summary>
    ItemOnly,
}
