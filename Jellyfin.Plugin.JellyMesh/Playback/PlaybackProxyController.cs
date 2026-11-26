using System;
using System.Linq;
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
    private const string MasterToFolder = "f/";
    private const string FolderToFolder = "../";

    private readonly PlaybackSessionStore _sessions;
    private readonly StubLookup _stubs;
    private readonly IAuthorizationContext _authorization;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlaybackProxyController> _logger;
    private readonly SessionRelay _relay;

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
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public PlaybackProxyController(
        PlaybackSessionStore sessions,
        PlaybackCoordinator coordinator,
        StubLookup stubs,
        IAuthorizationContext authorization,
        TimeProvider clock,
        PlaybackRefusal refusal,
        ILogger<PlaybackProxyController> logger,
        ErrorLogLimits? errorLogs = null)
    {
        _logger = logger;
        _sessions = sessions;
        _stubs = stubs;
        _authorization = authorization;
        _clock = clock;
        _relay = new SessionRelay(coordinator, refusal, clock, logger, errorLogs);
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
            return Refused(session);
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
            return Refused(session);
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
            return Refused(session);
        }

        if (file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return await RelayPlaylistAsync(session, upstream, FolderOf(upstream), FolderToFolder, access.Token, cancellationToken).ConfigureAwait(false);
        }

        return await RelayStreamAsync(session, upstream, cancellationToken).ConfigureAwait(false);
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

    private Task<ActionResult> RelayPlaylistAsync(PlaybackSession session, string path, string remoteFolder, string prefix, string token, CancellationToken cancellationToken)
    {
        return _relay.PlaylistAsync(HttpContext, session, path, remoteFolder, prefix, token, SessionRelay.PlaylistType, cancellationToken);
    }

    private Task<ActionResult> RelayStreamAsync(PlaybackSession session, string path, CancellationToken cancellationToken)
    {
        return _relay.StreamAsync(HttpContext, session, path, cancellationToken);
    }

    // A closed session is gone for its client, one with too many requests running is busy.
    private ActionResult Refused(PlaybackSession session)
    {
        return session.IsClosed ? Closed() : TooManyRequests(session);
    }

    private NotFoundResult Closed()
    {
        _logger.LogDebug("A playback request reached a play session while it was closed: 404");
        return NotFound();
    }

    private StatusCodeResult TooManyRequests(PlaybackSession session)
    {
        _logger.LogWarning("Play session {PlaySessionId} already has {Count} requests running: one more is refused with 503", session.Id, PlaybackLimits.RequestsPerSession);
        return StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private sealed record SessionAccess(PlaybackSession? Session, string Token);
}
