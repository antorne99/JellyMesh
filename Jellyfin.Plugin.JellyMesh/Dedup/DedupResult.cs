using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The outcome of a deduplication run. It describes the whole desired state: a stub with no matching
/// <see cref="FederationDecision.Winner"/> is out of date, which also covers plan §4.2 rules 1 and 5.
/// </summary>
/// <param name="Decisions">
/// One decision per identity with a remote source, sorted by key. Titles only found locally are left out. A file with
/// several episodes has one decision per episode.
/// </param>
/// <param name="Issues">The doubtful matches, sorted.</param>
public sealed record DedupResult(IReadOnlyList<FederationDecision> Decisions, IReadOnlyList<DedupIssue> Issues)
{
    /// <summary>
    /// Gets one local item per work that has local copies, by <see cref="ContentIdentity.WorkKey"/>. A series is completed
    /// per episode, so its stubs follow the local series even when no decision of it is local.
    /// </summary>
    public IReadOnlyDictionary<string, string> LocalItemByWork { get; init; } = new Dictionary<string, string>();
}
