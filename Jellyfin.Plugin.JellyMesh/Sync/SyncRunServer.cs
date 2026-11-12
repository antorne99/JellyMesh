using System;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// What one sync did for one server. Holds no address.
/// </summary>
/// <param name="ServerId">The server.</param>
/// <param name="ServerName">Its display name at the time.</param>
/// <param name="Read">How many catalog entries were read.</param>
/// <param name="Created">How many stubs of its folder were created.</param>
/// <param name="Updated">How many stubs of its folder were updated.</param>
/// <param name="Removed">How many stubs of its folder were removed.</param>
/// <param name="Failed">How many stubs of its folder could not be written or removed.</param>
/// <param name="Failure">Why it could not be read, null if it was.</param>
public sealed record SyncRunServer(
    Guid ServerId,
    string ServerName,
    int Read,
    int Created,
    int Updated,
    int Removed,
    int Failed,
    RemoteFailure? Failure = null);
