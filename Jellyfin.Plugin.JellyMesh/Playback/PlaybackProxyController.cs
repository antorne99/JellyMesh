using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// What clients play: playlists, segments, subtitles and streams of a session. Each request is checked again against the
/// user and the library of the title, and a remote failure ends in a bare status: remote bodies never pass.
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
    private readonly ILibraryManager _libraryManager;
    private readonly IAuthorizationContext _authorization;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackProxyController"/> class.
    /// </summary>
    /// <param name="sessions">The sessions.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="clock">The clock.</param>
    public PlaybackProxyController(
        PlaybackSessionStore sessions,
        PlaybackCoordinator coordinator,
        ILibraryManager libraryManager,
        IAuthorizationContext authorization,
        TimeProvider clock)
    {
        _sessions = sessions;
        _coordinator = coordinator;
        _libraryManager = libraryManager;
        _authorization = authorization;
        _clock = clock;
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
    public async Task<ActionResult> GetMaster([FromRoute] string sessionId, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(sessionId).ConfigureAwait(false);
        if (access.Session is not { Kind: PlaybackStreamKind.Hls } session)
        {
            return NotFound();
        }

        using var request = session.BeginRequest(_clock);
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
    public async Task<ActionResult> GetStream([FromRoute] string sessionId, CancellationToken cancellationToken)
    {
        var access = await AuthorizeAsync(sessionId).ConfigureAwait(false);
        if (access.Session is not { Kind: PlaybackStreamKind.Direct or PlaybackStreamKind.Progressive } session)
        {
            return NotFound();
        }

        using var request = session.BeginRequest(_clock);
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
            return NotFound();
        }

        using var request = session.BeginRequest(_clock);
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
        if (authorization.User is not { } user
            || string.IsNullOrEmpty(authorization.Token)
            || _sessions.Find(sessionId) is not { } session
            || session.Identity.UserId != user.Id
            || _libraryManager.GetItemById<BaseItem>(session.Identity.ItemId, user) is null)
        {
            return new SessionAccess(null, string.Empty);
        }

        return new SessionAccess(session, authorization.Token);
    }

    private async Task<ActionResult> RelayPlaylistAsync(PlaybackSession session, string path, string remoteFolder, string prefix, string token, CancellationToken cancellationToken)
    {
        var playlist = await _coordinator.ReadPlaylistAsync(session.Source.ServerId, path, cancellationToken).ConfigureAwait(false);
        var text = playlist.IsSuccess
            ? HlsPlaylistRewriter.Rewrite(playlist.Value!, session, remoteFolder, prefix, token, PlaybackCoordinator.SecretsOf(session))
            : null;
        return text is null
            ? StatusCode(StatusCodes.Status502BadGateway)
            : Content(text, PlaylistType);
    }

    private async Task<ActionResult> RelayStreamAsync(PlaybackSession session, string path, CancellationToken cancellationToken)
    {
        var method = HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        var range = Request.Headers.Range.ToString();
        var request = new RemoteStreamRequest(method, path, string.IsNullOrEmpty(range) ? null : range);
        var opened = await _coordinator.OpenAsync(session.Source.ServerId, request, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        using var stream = opened.Value!;
        await StreamRelay.CopyAsync(stream, HttpContext, cancellationToken).ConfigureAwait(false);
        return new EmptyResult();
    }

    private sealed record SessionAccess(PlaybackSession? Session, string Token);
}
