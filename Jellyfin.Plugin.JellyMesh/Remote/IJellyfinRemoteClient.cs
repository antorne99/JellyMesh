using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Calls to the API of a remote Jellyfin server.
/// </summary>
public interface IJellyfinRemoteClient
{
    /// <summary>
    /// Logs in with <c>AuthenticateByName</c>. Administrator accounts are refused.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="credentials">The dedicated user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The session.</returns>
    Task<RemoteResult<RemoteSession>> AuthenticateAsync(RemoteEndpoint endpoint, RemoteCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a session on the remote, so that its device leaves the list there. A failure is only reported.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session to end.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the remote ended it.</returns>
    Task<RemoteResult<bool>> LogoutAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Detects whether the remote runs JellyMesh. A 404 means standard mode.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The capabilities.</returns>
    Task<RemoteResult<RemoteCapabilities>> DetectCapabilitiesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the film and series libraries of the dedicated user.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries.</returns>
    Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the libraries a JellyMesh remote shares with this server.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries.</returns>
    Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetSharedLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of the change feed of a JellyMesh remote.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="cursor">Where to read from, null to learn the current position.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page.</returns>
    Task<RemoteResult<RemoteChangePage>> GetChangesAsync(RemoteEndpoint endpoint, RemoteSession session, string? cursor, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of <c>/Items</c>, or of its counterpart in the ProviderApi.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="query">The page to read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The page. <see cref="RemoteFailure.ResponseTooLarge"/> means the same page with a lower limit may fit.</returns>
    Task<RemoteResult<RemoteItemPage>> GetItemsAsync(RemoteEndpoint endpoint, RemoteSession session, RemoteItemQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads an image of an item.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="itemId">The item on the remote.</param>
    /// <param name="imageType">The image type, such as <c>Primary</c>.</param>
    /// <param name="imageIndex">The index among the images of that type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The image. An answer that is not an image is <see cref="RemoteFailure.InvalidResponse"/>.</returns>
    Task<RemoteResult<RemoteImage>> GetImageAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, string imageType, int imageIndex, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the clock of the remote, from the <c>Date</c> header of a light request.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The time on the remote, null if it answered without a <c>Date</c> header.</returns>
    Task<RemoteResult<DateTimeOffset?>> GetServerTimeAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken);
}
