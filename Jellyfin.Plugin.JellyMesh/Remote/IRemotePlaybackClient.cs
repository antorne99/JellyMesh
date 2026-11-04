using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The calls of a playback to a remote. Every request carries the token in a header and the same User-Agent, because
/// the remote hashes it into the transcode path, and no redirect is ever followed.
/// </summary>
public interface IRemotePlaybackClient
{
    /// <summary>
    /// Asks the remote how it would serve an item to the device of the client.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The answer of the remote.</returns>
    Task<RemoteResult<PlaybackInfoResponse>> GetPlaybackInfoAsync(RemoteEndpoint endpoint, RemoteSession session, RemotePlaybackRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Reads what the remote says about transcoding for the dedicated user.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="readHardware">Whether to read the hardware acceleration too. A JellyMesh remote keeps that route closed to peers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The policy.</returns>
    Task<RemoteResult<RemoteTranscodePolicy>> GetTranscodePolicyAsync(RemoteEndpoint endpoint, RemoteSession session, bool readHardware, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a playlist.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="path">The path and query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The text.</returns>
    Task<RemoteResult<string>> ReadPlaylistAsync(RemoteEndpoint endpoint, RemoteSession session, string path, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a stream. It ends when the headers arrive, so the token bounds the opening and not the transfer.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="request">What to open.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream, to dispose.</returns>
    Task<RemoteResult<RemoteContent>> OpenStreamAsync(RemoteEndpoint endpoint, RemoteSession session, RemoteStreamRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the transcode of a play session with <c>DELETE /Videos/ActiveEncodings</c>, which leaves the watch state of
    /// the remote user alone.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="playSessionId">The play session on the remote.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the remote accepted it.</returns>
    Task<RemoteResult<bool>> StopEncodingAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Gives a stream slot back to a JellyMesh remote. Only saves time: an unused slot expires there by itself.
    /// </summary>
    /// <param name="endpoint">The remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="playSessionId">The play session on the remote.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the remote accepted it.</returns>
    Task<RemoteResult<bool>> ReleaseStreamAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken);
}
