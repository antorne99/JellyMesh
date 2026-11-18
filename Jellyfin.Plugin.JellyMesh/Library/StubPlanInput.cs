using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a planner needs to know.
/// </summary>
/// <param name="View">The resolved catalog.</param>
/// <param name="Snapshot">The index the view was read from.</param>
/// <param name="State">The stubs that exist.</param>
/// <param name="Servers">The configured servers.</param>
/// <param name="IsProtected">Whether metadata protection is on.</param>
/// <param name="StrmUrl">Builds the content of a <c>.strm</c> from a stub ID.</param>
/// <param name="LocalSeriesOf">Finds the local series of a work key.</param>
public sealed record StubPlanInput(
    FederationView View,
    IndexSnapshot Snapshot,
    StubState State,
    IReadOnlyList<RemoteServerInfo> Servers,
    bool IsProtected,
    Func<string, string> StrmUrl,
    Func<string, LocalSeries?> LocalSeriesOf)
{
    /// <summary>Gets a value indicating whether films carry the collection of their remote.</summary>
    public bool WritesCollections { get; init; }

    /// <summary>
    /// Gets the local library a type folder of a remote library goes to, as a key: folders with one key hold one stub per
    /// title. By default one local library per server and type.
    /// </summary>
    public Func<Guid, string, TypeFolder, string> PlacementOf { get; init; } = DefaultPlacement;

    /// <summary>Gives the default placement: one local library per server and type.</summary>
    /// <param name="serverId">The server.</param>
    /// <param name="libraryId">The remote library.</param>
    /// <param name="type">The type folder.</param>
    /// <returns>The key.</returns>
    public static string DefaultPlacement(Guid serverId, string libraryId, TypeFolder type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Name;
    }
}
