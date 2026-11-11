using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The copies of one title that Jellyfin links as versions of one entity: at most one stub per server folder and the local
/// copy, if there is one. The group holds for the whole server, not per library. Holds no address and no token.
/// </summary>
public sealed record VersionGroup
{
    /// <summary>Gets the stable ID: random and minted once.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the last identity key seen. Only a hint: a group is found again by its members.</summary>
    public required string IdentityKey { get; init; }

    /// <summary>Gets the stubs, one per server.</summary>
    public IReadOnlyList<string> StubIds { get; init; } = [];

    /// <summary>Gets the local copies, empty if there is none.</summary>
    public IReadOnlyList<Guid> LocalItemIds { get; init; } = [];

    /// <summary>
    /// Gets the stub that is the primary version when there is no local copy, which is the primary otherwise. Null until
    /// the versions are first linked; kept while that stub stays in the group.
    /// </summary>
    public string? PrimaryStubId { get; init; }
}
