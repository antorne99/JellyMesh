using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The index read against the local catalog: per content identity, its remote sources (plan §3.1).
/// </summary>
/// <remarks>
/// An identity missing from <c>Contents</c> does not prove that a stub is out of date: the index may be empty
/// because it was lost, or a server may not have answered. Check <see cref="IndexSnapshot.IsAuthoritativeFor"/> first.
/// </remarks>
/// <param name="Health">The state of the index file.</param>
/// <param name="LocalItemCount">The number of real local copies compared.</param>
/// <param name="Contents">One entry per identity with a remote source. Titles found only locally are left out.</param>
/// <param name="Issues">The doubtful matches.</param>
public sealed record FederationView(
    IndexHealth Health,
    int LocalItemCount,
    IReadOnlyList<ResolvedContent> Contents,
    IReadOnlyList<DedupIssue> Issues);
