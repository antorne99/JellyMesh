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
}
