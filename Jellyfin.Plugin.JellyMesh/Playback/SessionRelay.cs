using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Relays what a session reads from its remote: playlists rewritten for the client, and streams with <c>Range</c>. A
/// remote failure ends in a bare status, and one that is not a missing subtitle or a full queue ends the session.
/// </summary>
public sealed class SessionRelay
{
    /// <summary>
    /// The type of the playlists of the session routes.
    /// </summary>
    public const string PlaylistType = "application/vnd.apple.mpegurl";

    private readonly PlaybackCoordinator _coordinator;
    private readonly PlaybackRefusal _refusal;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionRelay"/> class.
    /// </summary>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="refusal">The refusal of a playback.</param>
    /// <param name="clock">The clock.</param>
    public SessionRelay(PlaybackCoordinator coordinator, PlaybackRefusal refusal, TimeProvider clock)
    {
        _coordinator = coordinator;
        _refusal = refusal;
        _clock = clock;
    }

    /// <summary>
    /// Reads a playlist of a session from its remote and rewrites it for the client.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="session">The session.</param>
    /// <param name="path">The playlist on the remote.</param>
    /// <param name="remoteFolder">The folder of the playlist on the remote, ending with a slash.</param>
    /// <param name="prefix">What leads from the client's URL of the playlist to the folder route.</param>
    /// <param name="token">The local token.</param>
    /// <param name="contentType">The type to answer with.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The playlist, or a bare status.</returns>
    public async Task<ActionResult> PlaylistAsync(HttpContext http, PlaybackSession session, string path, string remoteFolder, string prefix, string token, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.HasFailed)
        {
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        var playlist = await _coordinator.ReadPlaylistAsync(session.Source.ServerId, path, cancellationToken).ConfigureAwait(false);
        if (!playlist.IsSuccess)
        {
            return Failed(http, session, path, playlist.Failure);
        }

        var text = HlsPlaylistRewriter.Rewrite(playlist.Value!, session, remoteFolder, prefix, token, PlaybackCoordinator.SecretsOf(session));
        return text is null
            ? new StatusCodeResult(StatusCodes.Status502BadGateway)
            : new ContentResult { Content = text, ContentType = contentType, StatusCode = StatusCodes.Status200OK };
    }

    /// <summary>
    /// Copies a stream of a session from its remote to the client, with the method and <c>Range</c> of the request.
    /// </summary>
    /// <param name="http">The request.</param>
    /// <param name="session">The session.</param>
    /// <param name="path">The stream on the remote.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An empty result once copied, or a bare status.</returns>
    public async Task<ActionResult> StreamAsync(HttpContext http, PlaybackSession session, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(session);

        if (session.HasFailed)
        {
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        var method = HttpMethods.IsHead(http.Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        var range = http.Request.Headers.Range.ToString();
        var request = new RemoteStreamRequest(method, path, string.IsNullOrEmpty(range) ? null : range);
        var opened = await _coordinator.OpenAsync(session.Source.ServerId, request, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Failed(http, session, path, opened.Failure);
        }

        using var stream = opened.Value!;
        if (await StreamRelay.CopyAsync(stream, http, _coordinator.StallLimit, cancellationToken, _clock).ConfigureAwait(false) && !IsSubtitle(path))
        {
            _coordinator.ReportFailure(session);
        }

        return new EmptyResult();
    }

    private static bool IsSubtitle(string path)
    {
        return path.Split('?')[0].Contains("/Subtitles/", StringComparison.OrdinalIgnoreCase);
    }

    // A stream that lost its slot and came back to a full server: the same refusal as a new PlaybackInfo gets. Any other
    // failure but a missing subtitle or a full queue here ends the session, so that the player reconnects (row 57).
    private ActionResult Failed(HttpContext http, PlaybackSession session, string path, RemoteFailure failure)
    {
        if (failure == RemoteFailure.StreamLimitReached)
        {
            return _refusal.Refuse(http, session.Identity.DeviceId, PlaybackRefusal.LimitText);
        }

        if (failure != RemoteFailure.Busy && !IsSubtitle(path))
        {
            _coordinator.ReportFailure(session);
        }

        return new StatusCodeResult(StatusCodes.Status502BadGateway);
    }
}
