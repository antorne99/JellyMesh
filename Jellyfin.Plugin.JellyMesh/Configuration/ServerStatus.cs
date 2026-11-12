using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A server as shown on the configuration page, to administrators only.
/// </summary>
/// <param name="Id">The local identifier.</param>
/// <param name="Name">The display name.</param>
/// <param name="Url">The base URL.</param>
/// <param name="Mode">The detected mode.</param>
/// <param name="ProtocolVersion">The JellyMesh protocol version.</param>
/// <param name="ImportedLibraryIds">The libraries to import.</param>
/// <param name="Connection">The connection state.</param>
/// <param name="LastCheckedUtc">When the connection was last used.</param>
public sealed record ServerStatus(
    Guid Id,
    string Name,
    string Url,
    RemoteMode Mode,
    int ProtocolVersion,
    IReadOnlyList<string> ImportedLibraryIds,
    string Connection,
    DateTimeOffset? LastCheckedUtc)
{
    /// <summary>
    /// Gets a value indicating whether the stored password no longer decrypts and must be entered again.
    /// </summary>
    public bool NeedsPassword { get; init; }

    /// <summary>
    /// Gets the former name of a removed server that an add recognized by its Server Id and gave its identity back.
    /// </summary>
    public string? RecognizedAs { get; init; }

    /// <summary>
    /// Gets the state of the Film and Serie libraries the plugin keeps for this server.
    /// </summary>
    public AutomaticLibrariesState? AutomaticLibraries { get; init; }
}
