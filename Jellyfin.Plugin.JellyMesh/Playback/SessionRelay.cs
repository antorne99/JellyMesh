using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger _logger;
    private readonly ErrorLogLimits _errorLogs;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionRelay"/> class.
    /// </summary>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="refusal">The refusal of a playback.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger of the routes that relay.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public SessionRelay(PlaybackCoordinator coordinator, PlaybackRefusal refusal, TimeProvider clock, ILogger logger, ErrorLogLimits? errorLogs = null)
    {
        _errorLogs = errorLogs ?? new ErrorLogLimits();
        _logger = logger;
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

        var part = PartOf(path, path == session.UpstreamPath);
        if (session.HasFailed)
        {
            return FailedBefore(session, part);
        }

        var playlist = await _coordinator.ReadPlaylistAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (!playlist.IsSuccess)
        {
            return session.IsClosed ? Closed(session) : Failed(http, session, path, part, playlist);
        }

        var text = HlsPlaylistRewriter.Rewrite(playlist.Value!, session, remoteFolder, prefix, token, PlaybackCoordinator.SecretsOf(session));
        if (text is null)
        {
            _logger.LogWarning("The {Part} of play session {PlaySessionId} on server {ServerId} could not be rewritten for the client: 502", part, session.Id, session.Source.ServerId);
            return new StatusCodeResult(StatusCodes.Status502BadGateway);
        }

        return new ContentResult { Content = text, ContentType = contentType, StatusCode = StatusCodes.Status200OK };
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

        var part = path == session.UpstreamPath ? "stream" : PartOf(path);
        if (session.HasFailed)
        {
            return FailedBefore(session, part);
        }

        var method = HttpMethods.IsHead(http.Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        var range = http.Request.Headers.Range.ToString();
        var request = new RemoteStreamRequest(method, path, string.IsNullOrEmpty(range) ? null : range, Device: session.RemoteDevice);
        var opened = await _coordinator.OpenAsync(session, request, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return session.IsClosed ? Closed(session) : Failed(http, session, path, part, opened);
        }

        using var stream = opened.Value!;
        var relayed = await StreamRelay.RelayAsync(stream, http, _coordinator.StallLimit, cancellationToken, _clock).ConfigureAwait(false);
        var ends = relayed.CountsAsFailure && !IsSubtitle(path);
        if (relayed.IsAbnormal)
        {
            var level = ends ? LogLevel.Warning : relayed.End == RelayEnd.ClientDropped ? LogLevel.Debug : LogLevel.Information;
            _logger.Log(
                level,
                "The {Part} of play session {PlaySessionId} ({Kind}, server {ServerId}) broke off: {End} after {Bytes} bytes in {Elapsed}, {Playing} into playback. Ends the session: {Ends}{Detail:l}",
                part,
                session.Id,
                session.Kind,
                session.Source.ServerId,
                relayed.End,
                relayed.Bytes,
                relayed.Elapsed,
                _clock.GetUtcNow() - session.Opened,
                ends,
                _logger.IsEnabled(level) ? _errorLogs.For(session.Source.ServerId).Of(relayed.Error) : string.Empty);
        }

        if (ends)
        {
            _coordinator.ReportFailure(session);
        }

        return new EmptyResult();
    }

    // Names the part of a stream a path is, for the logs.
    private static string PartOf(string path, bool isMaster = false)
    {
        var plain = path.Split('?')[0];
        return isMaster ? "master playlist"
            : IsSubtitle(path) ? "subtitle"
            : plain.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "playlist"
            : "segment";
    }

    private static bool IsSubtitle(string path)
    {
        return path.Split('?')[0].Contains("/Subtitles/", StringComparison.OrdinalIgnoreCase);
    }

    // A stream that lost its slot and came back to a full server: the same refusal as a new PlaybackInfo gets. Any other
    // failure but a missing subtitle or a full queue here ends the session, so that the player reconnects (row 57).
    private ActionResult Failed<T>(HttpContext http, PlaybackSession session, string path, string part, RemoteResult<T> result)
    {
        var failure = result.Failure;
        var ends = failure is not (RemoteFailure.StreamLimitReached or RemoteFailure.Busy) && !IsSubtitle(path);
        _logger.Log(
            ends ? LogLevel.Warning : LogLevel.Information,
            "The {Part} of play session {PlaySessionId} ({Kind}, server {ServerId}) did not open: {Failure}, {Playing} into playback. Ends the session: {Ends}",
            part,
            session.Id,
            session.Kind,
            session.Source.ServerId,
            result.Describe(),
            _clock.GetUtcNow() - session.Opened,
            ends);
        if (failure == RemoteFailure.StreamLimitReached)
        {
            return _refusal.Refuse(http, session.Identity.DeviceId, PlaybackRefusal.LimitText);
        }

        if (ends)
        {
            _coordinator.ReportFailure(session);
        }

        return new StatusCodeResult(StatusCodes.Status502BadGateway);
    }

    // A session closed while its call ran is gone for its client, and its call fails nothing.
    private NotFoundResult Closed(PlaybackSession session)
    {
        _logger.LogDebug("A call of play session {PlaySessionId} ended after the session closed: 404", session.Id);
        return new NotFoundResult();
    }

    // A retry of the player after its session failed (row 57): it gets 502 until it asks PlaybackInfo again.
    private StatusCodeResult FailedBefore(PlaybackSession session, string part)
    {
        _logger.LogDebug("The {Part} of failed play session {PlaySessionId} is refused: 502", part, session.Id);
        return new StatusCodeResult(StatusCodes.Status502BadGateway);
    }
}
