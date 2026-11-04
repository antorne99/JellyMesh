using System;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Encrypts remote credentials and reports whether they can be used at all.
/// </summary>
/// <remarks>
/// Sync and remote playback stop whenever <see cref="GetKeyState"/> is not <see cref="KeyState.Ok"/>.
/// <see cref="IRemoteServerRegistry"/> enforces this on every call, so remote work must go through it.
/// </remarks>
public interface ICredentialVault
{
    /// <summary>
    /// Evaluates the key against everything stored. Evaluated on each call, nothing is cached.
    /// </summary>
    /// <returns>The state of the key.</returns>
    KeyState GetKeyState();

    /// <summary>
    /// Encrypts credentials for a server.
    /// </summary>
    /// <param name="serverId">The server the credentials belong to.</param>
    /// <param name="credentials">The credentials.</param>
    /// <returns>The encrypted value, or null if the key state is not <see cref="KeyState.Ok"/>.</returns>
    string? Protect(Guid serverId, RemoteCredentials credentials);

    /// <summary>
    /// Decrypts the stored credentials of a server.
    /// </summary>
    /// <param name="serverId">The server the credentials belong to.</param>
    /// <param name="encryptedCredentials">The stored value.</param>
    /// <returns>The credentials, or null if the key state is not <see cref="KeyState.Ok"/>.</returns>
    RemoteCredentials? Unprotect(Guid serverId, string encryptedCredentials);
}
