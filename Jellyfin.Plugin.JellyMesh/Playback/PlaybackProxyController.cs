using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// What clients play: playlists, segments, subtitles and streams of a session. Each request is checked again against the
/// user and the library of the title, which a user who is not an admin must find in a library (D17), and a remote failure
/// ends in a bare status: remote bodies never pass. A refusal for the stream limit also tells the device why.
/// </summary>
[ApiController]
[Route(PlaybackUrls.SessionRoute + "/{sessionId}")]
[Authorize]
public class PlaybackProxyController : ControllerBase
{
    private const string PlaylistType = "application/vnd.apple.mpegurl";
    private const string MasterToFolder = "f/";
    private const string FolderToFolder = "../";

    private readonly PlaybackSessionStore _sessions;
    private readonly PlaybackCoordinator _coordinator;
    private readonly StubLookup _stubs;
    private readonly IAuthorizationContext _authorization;
    private readonly TimeProvider _clock;
    private readonly PlaybackRefusal _refusal;
    private readonly ILogger<PlaybackProxyController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackProxyController"/> class.
    /// </summary>
    /// <param name="sessions">The sessions.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="stubs">The stubs, for what a user sees.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="refusal">The refusal of a playback.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackProxyController(
        PlaybackSessionStore sessions,
        PlaybackCoordinator coordinator,
        StubLookup stubs,
        IAuthorizationContext authorization,
        TimeProvider clock,
        PlaybackRefusal refusal,
        ILogger<PlaybackProxyController> logger)
    {
        _logger = logger;
        _sessions = sessions;
        _coordinator = coordinator;
        _stubs = stubs;
        _authorization = authorization;
        _clock = clock;
        _refusal = refusal;
    }

    /// <summary>
    /// Gets the master playlist.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The playlist.</returns>
    [HttpGet("master.m3u8")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetMaster([FromRoute] string sessionId, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(sessionId).ConfigureAwait(false);
        if (access.Session is not { Kind: PlaybackStreamKind.Hls } session)
        {
            return NotFound();
        }

        using var request = session.TryBeginRequest(_clock, cancellationToken);
        if (request is null)
        {
            return TooManyRequests(session);
        }

        return await RelayPlaylistAsync(session, session.UpstreamPath, session.UpstreamFolder(), MasterToFolder, access.Token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the stream of a session that is not HLS, with <c>Range</c>.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stream.</returns>
    [HttpGet("stream.{container}")]
    [HttpHead("stream.{container}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetStream([FromRoute] string sessionId, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(sessionId).ConfigureAwait(false);
        if (access.Session is not { Kind: PlaybackStreamKind.Direct or PlaybackStreamKind.Progressive } session)
        {
            return NotFound();
        }

        using var request = session.TryBeginRequest(_clock, cancellationToken);
        if (request is null)
        {
            return TooManyRequests(session);
        }

        return await RelayStreamAsync(session, session.UpstreamPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a playlist, a segment or a subtitle that the session handed out.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="folder">The folder number.</param>
    /// <param name="file">The file name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The content.</returns>
    [HttpGet("f/{folder:int}/{file}")]
    [HttpHead("f/{folder:int}/{file}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> GetFile([FromRoute] string sessionId, [FromRoute] int folder, [FromRoute] string file, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(sessionId).ConfigureAwait(false);
        if (access.Session is not { } session)
        {
            return NotFound();
        }

        var clientQuery = Request.Query
            .Where(pair => !pair.Key.Equals("ApiKey", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new System.Collections.Generic.KeyValuePair<string, string>(pair.Key, pair.Value.ToString()));
        if (session.FindUpstream(folder, file, clientQuery) is not { } upstream)
        {
            _logger.LogDebug("Play session {PlaySessionId} handed out no such file: 404", session.Id);
            return NotFound();
        }

        using var request = session.TryBeginRequest(_clock, cancellationToken);
        if (request is null)
        {
            return TooManyRequests(session);
        }

        if (file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return await RelayPlaylistAsync(session, upstream, FolderOf(upstream), FolderToFolder, access.Token, cancellationToken).ConfigureAwait(false);
        }

        return await RelayStreamAsync(session, upstream, cancellationToken).ConfigureAwait(false);
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

    private static string FolderOf(string path)
    {
        var plain = path.Split('?')[0];
        return plain[..(plain.LastIndexOf('/') + 1)];
    }

    private async Task<SessionAccess> AuthorizeAsync(string sessionId)
    {
        var authorization = await _authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (authorization.User is not { } user || string.IsNullOrEmpty(authorization.Token))
        {
            _logger.LogDebug("A playback request came without a user: 404");
            return new SessionAccess(null, string.Empty);
        }

        if (_sessions.Find(sessionId) is not { } session)
        {
            _logger.LogDebug("A playback request named a play session that does not exist or was closed: 404");
            return new SessionAccess(null, string.Empty);
        }

        if (session.Identity.UserId != user.Id || !_stubs.Sees(user, session.Identity.ItemId))
        {
            _logger.LogWarning("A user asked for play session {PlaySessionId} of another user or of a title they do not see: 404", session.Id);
            return new SessionAccess(null, string.Empty);
        }

        return new SessionAccess(session, authorization.Token);
    }

    private async Task<ActionResult> RelayPlaylistAsync(PlaybackSession session, string path, string remoteFolder, string prefix, string token, CancellationToken cancellationToken)
    {
        var part = PartOf(path, prefix == MasterToFolder);
        if (session.HasFailed)
        {
            return FailedBefore(session, part);
        }

        var playlist = await _coordinator.ReadPlaylistAsync(session.Source.ServerId, path, cancellationToken).ConfigureAwait(false);
        if (!playlist.IsSuccess)
        {
            return RelayFailed(session, path, part, playlist);
        }

        var text = HlsPlaylistRewriter.Rewrite(playlist.Value!, session, remoteFolder, prefix, token, PlaybackCoordinator.SecretsOf(session));
        if (text is null)
        {
            _logger.LogWarning("The {Part} of play session {PlaySessionId} on server {ServerId} could not be rewritten for the client: 502", part, session.Id, session.Source.ServerId);
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        return Content(text, PlaylistType);
    }

    private async Task<ActionResult> RelayStreamAsync(PlaybackSession session, string path, CancellationToken cancellationToken)
    {
        var part = path == session.UpstreamPath ? "stream" : PartOf(path);
        if (session.HasFailed)
        {
            return FailedBefore(session, part);
        }

        var method = HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        var range = Request.Headers.Range.ToString();
        var request = new RemoteStreamRequest(method, path, string.IsNullOrEmpty(range) ? null : range);
        var opened = await _coordinator.OpenAsync(session.Source.ServerId, request, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return RelayFailed(session, path, part, opened);
        }

        using var stream = opened.Value!;
        var relayed = await StreamRelay.RelayAsync(stream, HttpContext, _coordinator.StallLimit, cancellationToken, _clock).ConfigureAwait(false);
        var ends = relayed.CountsAsFailure && !IsSubtitle(path);
        if (relayed.IsAbnormal)
        {
            _logger.Log(
                ends ? LogLevel.Warning : relayed.End == RelayEnd.ClientDropped ? LogLevel.Debug : LogLevel.Information,
                "The {Part} of play session {PlaySessionId} ({Kind}, server {ServerId}) broke off: {End} after {Bytes} bytes in {Elapsed}, {Playing} into playback. Ends the session: {Ends}",
                part,
                session.Id,
                session.Kind,
                session.Source.ServerId,
                relayed.End,
                relayed.Bytes,
                relayed.Elapsed,
                _clock.GetUtcNow() - session.Opened,
                ends);
        }

        if (ends)
        {
            _coordinator.ReportFailure(session);
        }

        return new EmptyResult();
    }

    // A retry of the player after its session failed (row 57): it gets 502 until it asks PlaybackInfo again.
    private StatusCodeResult FailedBefore(PlaybackSession session, string part)
    {
        _logger.LogDebug("The {Part} of failed play session {PlaySessionId} is refused: 502", part, session.Id);
        return StatusCode(StatusCodes.Status502BadGateway);
    }

    private StatusCodeResult TooManyRequests(PlaybackSession session)
    {
        _logger.LogWarning("Play session {PlaySessionId} already has {Count} requests running: one more is refused with 503", session.Id, PlaybackLimits.RequestsPerSession);
        return StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // A stream that lost its slot and came back to a full server: the same refusal as a new PlaybackInfo gets. Any other
    // failure but a missing subtitle or a full queue here ends the session, so that the player reconnects (row 57).
    private ActionResult RelayFailed<T>(PlaybackSession session, string path, string part, RemoteResult<T> result)
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
            return _refusal.Refuse(HttpContext, session.Identity.DeviceId, PlaybackRefusal.LimitText);
        }

        if (ends)
        {
            _coordinator.ReportFailure(session);
        }

        return StatusCode(StatusCodes.Status502BadGateway);
    }

    private sealed record SessionAccess(PlaybackSession? Session, string Token);
}
