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
    /// configured server fails with <see cref="RemoteFailure.AlreadyConfigured"/> and that server as the value. A server
    /// added again after a removal is a new one, with a new identifier (decision 408).
    /// </summary>
    /// <param name="name">The display name.</param>
    /// <param name="url">The base URL.</param>
    /// <param name="credentials">The dedicated user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The added server.</returns>
    Task<RemoteResult<RemoteServerInfo>> AddAsync(string name, string url, RemoteCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Checks an edit of a server without saving anything (row 432): logs in with the new data on a device of its own, so
    /// the kept login stays, accepts only the saved Server Id (row 433), reads the libraries the user sees, then ends that
    /// login within a short time. A refusal names the field it is about and, for another server, the name it gives itself.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="edit">The fields of the form.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the edit changes and what the remote offers, or why it is refused.</returns>
    Task<RemoteResult<RemoteEditReport>> CheckEditAsync(Guid serverId, RemoteServerEdit edit, CancellationToken cancellationToken);

    /// <summary>
    /// Saves an edit of a server all or nothing (row 432): checks it as <see cref="CheckEditAsync"/> does, then saves name,
    /// address, credentials, imported libraries and capabilities in one write. A new user or password logs in on the kept
    /// device and keeps that login; otherwise the kept login stays. Only a new name needs no remote. After the save of a new
    /// user, runs <paramref name="stopStreams"/>, then ends the login of the old user within a short time (row 434).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="edit">The fields of the form.</param>
    /// <param name="stopStreams">What stops the streams of the server when its user changes, or null.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the edit changed, or why it is refused.</returns>
    Task<RemoteResult<RemoteEditReport>> SaveEditAsync(Guid serverId, RemoteServerEdit edit, Func<Task>? stopStreams, CancellationToken cancellationToken);

    /// <summary>
    /// Enters the edit of a server, so that its checks and saves run one at a time and the last save wins (row 434).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What leaves the edit when disposed, or null while the server is being removed.</returns>
    Task<IDisposable?> EnterEditAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a server as being removed, so that edits entered from now on are refused, then waits for the edit in progress
    /// to end (row 434).
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What takes the mark away when disposed, once the removal ended.</returns>
    Task<IDisposable> BeginRemovalAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a server. Possible in any key state: it uses no credential, and it is how to
    /// recover from a lost key. Then ends the live login on the remote, if there is one, within a short time (413): if that
    /// fails, its device stays listed there and the removal stands.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>Whether the server existed.</returns>
    /// <exception cref="System.Exception">The configuration could not be saved. The server stays, unless the save was cancelled.</exception>
    Task<bool> RemoveAsync(Guid serverId);

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
