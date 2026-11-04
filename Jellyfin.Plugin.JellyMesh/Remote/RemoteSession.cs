using System;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// An authenticated session on a remote. A class rather than a record, so that no generated
/// <see cref="object.ToString"/> can print the token.
/// </summary>
public sealed class RemoteSession
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteSession"/> class.
    /// </summary>
    /// <param name="accessToken">The access token.</param>
    /// <param name="userId">The remote user identifier.</param>
    public RemoteSession(string accessToken, string userId)
    {
        AccessToken = accessToken;
        UserId = userId;
    }

    /// <summary>
    /// Gets the access token. It stays in memory, is never persisted and never leaves the server.
    /// </summary>
    public string AccessToken { get; }

    /// <summary>
    /// Gets the remote user identifier.
    /// </summary>
    public string UserId { get; }
}
