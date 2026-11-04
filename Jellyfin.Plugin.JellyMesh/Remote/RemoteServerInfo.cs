using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// A configured server as seen outside the registry. Holds nothing secret.
/// </summary>
/// <param name="Id">The local identifier.</param>
/// <param name="Name">The display name.</param>
/// <param name="Url">The base URL. Only for administrators, never for clients.</param>
/// <param name="Mode">The last detected mode.</param>
/// <param name="ProtocolVersion">The JellyMesh protocol version.</param>
/// <param name="ImportedLibraryIds">The libraries to import.</param>
/// <param name="LastFailure">The result of the last operation, null if there was none since start.</param>
/// <param name="LastCheckedUtc">When that operation ended.</param>
public sealed record RemoteServerInfo(
    Guid Id,
    string Name,
    string Url,
    RemoteMode Mode,
    int ProtocolVersion,
    IReadOnlyList<string> ImportedLibraryIds,
    RemoteFailure? LastFailure,
    DateTimeOffset? LastCheckedUtc);
