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
    /// Gets the local copies, empty unless <see cref="IsLocal"/>. A stub being replaced needs them to hand its watch
    /// state over.
    /// </summary>
    public IReadOnlyList<string> LocalItemIds { get; init; } = [];

    /// <summary>
    /// Gets every remote item of this identity, whatever was decided. <see cref="Winner"/> and <see cref="Backups"/> are
    /// empty when a local copy exists, and a stub still has to be found from its items.
    /// </summary>
    public IReadOnlyList<RemoteItemKey> RemoteItems { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether a local copy exists, so the local copy wins.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Winner))]
    public bool IsLocal => Winner is null;
}
