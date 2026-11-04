using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A film or episode stub. Holds no address and no token.
/// </summary>
public sealed record StubEntry
{
    /// <summary>
    /// Gets the stable ID: random, minted once, and independent of <c>ContentIdentity.Key</c> and of the server.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Gets the kind of stub.</summary>
    public required StubKind Kind { get; init; }

    /// <summary>
    /// Gets the path of the <c>.strm</c> relative to the federated folder. Fixed at creation: the ID of the Jellyfin item
    /// is a hash of it, so a new path would be a new item with no watch state.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>Gets the sources: the winner first, then the backups, best first. Never empty.</summary>
    public required IReadOnlyList<StubSource> Sources { get; init; }

    /// <summary>Gets the last identity key seen. Only a hint to find the stub again: a key can change.</summary>
    public required string IdentityKey { get; init; }

    /// <summary>Gets the stub series of an episode.</summary>
    public string? SeriesStubId { get; init; }

    /// <summary>Gets the season of an episode.</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the number of an episode.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets a hash of what was written for the stub, null until it is on disk.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Gets a tag of the images written, null until they are on disk.</summary>
    public string? ImageTag { get; init; }

    /// <summary>Gets a tag of the technical data applied to the item.</summary>
    public string? TechnicalTag { get; init; }
}
