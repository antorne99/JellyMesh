using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The configured remote servers and the only way to act on them. Every operation that talks to a
/// remote fails with <see cref="RemoteFailure.Suspended"/>, without sending anything, when the key
/// state is not <see cref="KeyState.Ok"/>.
/// </summary>
public interface IRemoteServerRegistry
{
    /// <summary>
    /// Lists the configured servers.
    /// </summary>
    /// <returns>The servers.</returns>
    IReadOnlyList<RemoteServerInfo> GetServers();

    /// <summary>
    /// Adds a server, once the login on it succeeded. On failure nothing is saved. A remote whose Server Id belongs to a
    /// configured server fails with <see cref="RemoteFailure.AlreadyConfigured"/> and that server as the value; one that
    /// was removed gets its identifier back, with its former name in <see cref="RemoteServerInfo.RecognizedAs"/>.
    /// </summary>
    /// <param name="name">The display name.</param>
    /// <param name="url">The base URL.</param>
    /// <param name="credentials">The dedicated user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The added server.</returns>
    Task<RemoteResult<RemoteServerInfo>> AddAsync(string name, string url, RemoteCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the dedicated user of a server, once the login with it succeeded. On failure nothing is saved. Works with a
    /// wrong key too, because entering each password again is how a lost key file is fixed.
    /// </summary>
    /// <param name="serverId">The server to update.</param>
    /// <param name="credentials">The dedicated user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The server with its new credentials.</returns>
    Task<RemoteResult<RemoteServerInfo>> UpdateCredentialsAsync(Guid serverId, RemoteCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a server. Possible in any key state: it uses no credential, and it is how to
    /// recover from a lost key.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>Whether the server existed.</returns>
    /// <exception cref="System.Exception">The configuration could not be saved. The server stays, unless the save was cancelled.</exception>
    bool Remove(Guid serverId);

    /// <summary>
    /// Renames a server. Its ID, and so its folder, stay the same (Q5, Q38).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="name">The new name, trimmed.</param>
    /// <returns>Whether the server existed.</returns>
    /// <exception cref="System.ArgumentException">The name is blank.</exception>
    bool Rename(Guid serverId, string name);

    /// <summary>
    /// Tests the login and refreshes the capabilities of a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    Task<RemoteResult<ConnectionReport>> TestAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Detects the capabilities of a server again and records them. A sync does this first: what a remote offers changes
    /// without notice, and a JellyMesh remote closes the stock catalog to a server once it is connected there.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The server as it stands. A failed detection leaves it as it was.</returns>
    Task<RemoteServerInfo?> RefreshCapabilitiesAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the importable libraries of a server: what a JellyMesh remote shares, or what the dedicated user sees.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries.</returns>
    Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetLibrariesAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the libraries to import from a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="libraryIds">The remote library identifiers.</param>
    /// <returns>Whether the server exists.</returns>
    bool SetImportedLibraries(Guid serverId, IEnumerable<string> libraryIds);

    /// <summary>
    /// Runs a call with an authenticated session, logging in again once if the token was refused.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="serverId">The server.</param>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of <paramref name="call"/>, or why it could not run.</returns>
    Task<RemoteResult<T>> RunWithSessionAsync<T>(
        Guid serverId,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a playback call like <see cref="RunWithSessionAsync{T}"/>, without telling the activity log that the server
    /// went offline or came back (decision 130).
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="serverId">The server.</param>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of <paramref name="call"/>, or why it could not run.</returns>
    Task<RemoteResult<T>> RunPlaybackCallAsync<T>(
        Guid serverId,
        Func<RemoteEndpoint, RemoteSession, CancellationToken, Task<RemoteResult<T>>> call,
        CancellationToken cancellationToken);
}
