using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// The sources of one content identity.
/// </summary>
/// <param name="Identity">The identity. It holds for this result only: see <see cref="ContentIdentity.Key"/>.</param>
/// <param name="IsLocal">Whether a local copy exists, so that nothing is materialized.</param>
/// <param name="Winner">The source to materialize, null when a local copy exists.</param>
/// <param name="Backups">The other sources, best first.</param>
public sealed record ResolvedContent(
    ContentIdentity Identity,
    bool IsLocal,
    ResolvedSource? Winner,
    IReadOnlyList<ResolvedSource> Backups)
{
    /// <summary>Gets the local copies, empty unless a local copy exists.</summary>
    public IReadOnlyList<Guid> LocalItemIds { get; init; } = [];

    /// <summary>Gets every remote item of this identity, whatever was decided.</summary>
    public IReadOnlyList<ResolvedItem> RemoteItems { get; init; } = [];

    /// <summary>
    /// Gets the last episode, when this is a group of episodes starting at the one of <see cref="Identity"/>. The resolver
    /// never sets it: see <see cref="Library.StubContents"/>.
    /// </summary>
    public int? EpisodeNumberEnd { get; init; }
}
