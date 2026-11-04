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
    DateTimeOffset? LastCheckedUtc);
