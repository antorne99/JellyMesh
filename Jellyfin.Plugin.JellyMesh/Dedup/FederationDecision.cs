using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// What to do with one content identity that has at least one remote source (plan §4.2).
/// </summary>
/// <param name="Identity">The identity.</param>
/// <param name="Winner">The source to materialize, or <c>null</c> when a local copy exists and nothing is materialized.</param>
/// <param name="Backups">The other remote sources, best first. Empty when a local copy exists.</param>
public sealed record FederationDecision(
    ContentIdentity Identity,
    RemoteSource? Winner,
    IReadOnlyList<RemoteSource> Backups)
{
    /// <summary>
    /// Gets a value indicating whether a local copy exists, so the local copy wins.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Winner))]
    public bool IsLocal => Winner is null;
}
