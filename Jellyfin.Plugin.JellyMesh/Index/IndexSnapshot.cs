using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// An immutable copy of the index.
/// </summary>
public sealed record IndexSnapshot
{
    /// <summary>
    /// The current file format.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>Gets the file format.</summary>
    public required int Version { get; init; }

    /// <summary>Gets the state of the index file.</summary>
    public required IndexHealth Health { get; init; }

    /// <summary>Gets the servers.</summary>
    public required IReadOnlyList<ServerCatalog> Servers { get; init; }

    /// <summary>
    /// Creates an empty index.
    /// </summary>
    /// <param name="health">The state of the index file.</param>
    /// <returns>The index.</returns>
    public static IndexSnapshot Empty(IndexHealth health) => new() { Version = CurrentVersion, Health = health, Servers = [] };

    /// <summary>
    /// Finds a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The catalog of the server, null if it never synced.</returns>
    public ServerCatalog? Find(Guid serverId) => Servers.FirstOrDefault(server => server.ServerId == serverId);

    /// <summary>
    /// Tells whether an entry missing from a server is really gone from it. Anything that removes a stub must ask this
    /// first: an index that is empty because it was lost, or a server that did not answer, proves nothing.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>Whether the entries of the server are a complete read of its catalog.</returns>
    public bool IsAuthoritativeFor(Guid serverId) => Find(serverId) is { State: ServerSyncState.Complete };
}
