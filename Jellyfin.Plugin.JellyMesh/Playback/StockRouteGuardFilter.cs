using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Covers the stock media routes for stubs. Several of them are anonymous in Jellyfin, and for a stub the server itself
/// reads the <c>.strm</c> from loopback, so without this anyone with an item ID would read remote data. Also learns of a
/// stop from <c>DELETE /Videos/ActiveEncodings</c>, which is how jellyfin-web ends a transcode. Stock stops a play session
/// for anyone who names it, so a stub played on this server is kept out of the reach of other users here. So are the
/// play sessions a peer opened here and their devices, since stock stops every job of a device for a segment with no play
/// session. For a title with linked versions, every item a request names must be visible to its user. The legacy segment,
/// audio segment and playlist routes serve a file of the transcode folder by name, so they name the job it belongs to.
/// </summary>
public sealed class StockRouteGuardFilter : IAsyncActionFilter
{
    private const string StopEncodingAction = "StopEncodingProcess";

    private const string LyricsController = "Lyrics";

    private const string GetLyricsAction = "GetLyrics";

    private const string DownloadAction = "GetDownload";

    private const string GetFileAction = "GetFile";

    private const string LegacySegmentAction = "GetHlsVideoSegmentLegacy";

    private const string LegacyAudioAction = "GetHlsAudioSegmentLegacy";

    private const string LegacyPlaylistAction = "GetHlsPlaylistLegacy";

    /// <summary>
    /// The start of the name a refused legacy request reaches stock with, which no file of the transcode folder has.
    /// </summary>
    internal const string MissingFilePrefix = "jellymesh-missing-";

    private static readonly HashSet<string> _mediaControllers = new(StringComparer.Ordinal)
    {
        "Videos",
        "DynamicHls",
        "Audio",
        "UniversalAudio",
        "Subtitle",
        "VideoAttachments",
        "Trickplay",
        "MediaSegments"
    };

    // The routes a stub never reaches stock on: stock would send the text of the .strm, with its signed loopback URL, or
    // write lyrics inside the federated folder. A route of a new stub type is one entry here.
    private static readonly StubRoute[] _stubRoutes =
    [
        new("Library", GetFileAction, StubCover.Any, StubAccess.Visible, (filter, context, authorization, stubs) => filter.FileResultAsync(context, authorization, stubs[0])),
        new("Library", DownloadAction, StubCover.Any, StubAccess.Download, (filter, context, authorization, stubs) => filter.FileResultAsync(context, authorization, stubs[0])),
        new(LyricsController, null, StubCover.Audio, StubAccess.Visible, (filter, context, authorization, stubs) => filter.LyricsResultAsync(context, authorization, stubs[0])),
        new("Videos", null, StubCover.Audio, StubAccess.Visible, (filter, context, authorization, stubs) => filter.AudioStubResultAsync(context, authorization, stubs)),
        new("DynamicHls", null, StubCover.Audio, StubAccess.Visible, (filter, context, authorization, stubs) => filter.AudioStubResultAsync(context, authorization, stubs)),
        new("Audio", null, StubCover.Audio, StubAccess.Visible, (filter, context, authorization, stubs) => filter.AudioStubResultAsync(context, authorization, stubs)),
        new("UniversalAudio", null, StubCover.Audio, StubAccess.Visible, (filter, context, authorization, stubs) => filter.AudioStubResultAsync(context, authorization, stubs))
    ];

    private readonly StubLookup _stubs;
    private readonly IAuthorizationContext _authorization;
    private readonly PlaybackCoordinator _coordinator;
    private readonly PlaybackSessionStore _sessions;
    private readonly PlaybackRefusal _refusal;
    private readonly ITranscodeManager _transcodes;
    private readonly StreamSlots _peerSlots;
    private readonly Func<string, IReadOnlyList<string>> _listFolder;
    private readonly AudioStubRoutes _audio;

    /// <summary>
    /// Initializes a new instance of the <see cref="StockRouteGuardFilter"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="refusal">The refusal.</param>
    /// <param name="transcodes">The transcode manager.</param>
    /// <param name="peerSlots">The play sessions of the peers, when this server is their provider.</param>
    /// <param name="audio">The audio routes of track stubs.</param>
    public StockRouteGuardFilter(
        StubLookup stubs,
        IAuthorizationContext authorization,
        PlaybackCoordinator coordinator,
        PlaybackSessionStore sessions,
        PlaybackRefusal refusal,
        ITranscodeManager transcodes,
        StreamSlots peerSlots,
        AudioStubRoutes audio)
        : this(stubs, authorization, coordinator, sessions, refusal, transcodes, peerSlots, audio, ListFolder)
    {
    }

    // Takes how a folder is listed, so that tests can count the listings.
    internal StockRouteGuardFilter(
        StubLookup stubs,
        IAuthorizationContext authorization,
        PlaybackCoordinator coordinator,
        PlaybackSessionStore sessions,
        PlaybackRefusal refusal,
        ITranscodeManager transcodes,
        StreamSlots peerSlots,
        AudioStubRoutes audio,
        Func<string, IReadOnlyList<string>> listFolder)
    {
        _stubs = stubs;
        _authorization = authorization;
        _coordinator = coordinator;
        _sessions = sessions;
        _refusal = refusal;
        _transcodes = transcodes;
        _peerSlots = peerSlots;
        _listFolder = listFolder;
        _audio = audio;
    }

    /// <summary>
    /// Gets the routes a stub never reaches stock on, with the stubs each covers and the access it wants.
    /// </summary>
    internal static IReadOnlyList<StubRoute> StubRoutes => _stubRoutes;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var (controller, action) = StubLookup.ActionOf(context);
        if (IsSegment(controller, action) && string.IsNullOrWhiteSpace(PlaySessionOf(context)))
        {
            // A segment without a play session makes stock stop every job of its device, whoever asks; with no device, the
            // jobs that run without one.
            var caller = (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User?.Id ?? Guid.Empty;
            var device = DeviceOf(context);
            if (string.IsNullOrWhiteSpace(device)
                ? HasJobWithoutDevice(_sessions.LocalSessionsOfOthers(caller).Concat(_peerSlots.SessionsOfOthers(caller)))
                : _sessions.IsLocalDeviceOfAnother(caller, device) || _peerSlots.IsDeviceOfAnother(caller, device))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                return;
            }
        }

        if (IsAnonymousLegacy(controller, action) && (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User is null)
        {
            // D9: stock ignores the item of the route, so naming one proves nothing.
            context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
            return;
        }

        var named = IsMedia(controller, action) ? MediaPlaySessionsOf(context, controller, action) : [];
        if (await VersionRefusalAsync(context, controller, action, named).ConfigureAwait(false) is { } hidden)
        {
            await RefuseAsync(context, next, hidden, controller, action).ConfigureAwait(false);
            return;
        }

        if (IsStop(controller, action))
        {
            var caller = (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User?.Id;
            var playSessionId = PlaySessionOf(context);
            if (caller is { } user && (_sessions.IsLocalOfAnother(user, playSessionId) || _peerSlots.IsOfAnother(user, playSessionId)))
            {
                // What stock answers for a play session it does not know.
                context.Result = new NoContentResult();
                return;
            }

            if (action == StopEncodingAction && caller is { } owner && _sessions.Find(playSessionId) is { } session && session.Identity.UserId == owner)
            {
                await _coordinator.CloseAsync(session.Id).ConfigureAwait(false);
            }

            await next().ConfigureAwait(false);
            if (caller is { } stopper)
            {
                _sessions.EndLocal(stopper, playSessionId);
            }

            return;
        }
        else if (IsReport(controller, action) && IsRecorded(PlaySessionOf(context)))
        {
            if ((await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User is { } reporter)
            {
                // A report of another user would keep the stock job of the owner alive.
                if (_sessions.IsLocalOfAnother(reporter.Id, PlaySessionOf(context)) || _peerSlots.IsOfAnother(reporter.Id, PlaySessionOf(context)))
                {
                    context.Result = new NoContentResult();
                    return;
                }

                _sessions.UseLocal(reporter.Id, PlaySessionOf(context));
            }
        }
        else if (IsMedia(controller, action) && await PeerSessionRefusalAsync(context, named).ConfigureAwait(false) is { } refusal)
        {
            await RefuseAsync(context, next, refusal, controller, action).ConfigureAwait(false);
            return;
        }
        else if (StubRouteOf(context, controller, action, out var covered) is { } stubRoute)
        {
            var authorization = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            context.Result = AccessRefusal(authorization, covered, stubRoute.Access)
                ?? await stubRoute.Answer(this, context, authorization, covered).ConfigureAwait(false);
            return;
        }
        else if (IsMedia(controller, action)
            && StubsOf(context, named, out var recorded, out var guarded) is var stubs
            && (guarded.Count > 0 || recorded))
        {
            var authorization = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);

            // A segment that restarts ffmpeg stops the job of the play session it names, as the stop route does.
            var denied = AccessRefusal(authorization, guarded, StubAccess.Visible);
            if (denied is null && named.Any(id => _sessions.IsLocalOfAnother(authorization.User!.Id, id)))
            {
                denied = new StatusCodeResult(StatusCodes.Status404NotFound);
            }

            if (denied is not null)
            {
                await RefuseAsync(context, next, denied, controller, action).ConfigureAwait(false);
                return;
            }

            var user = authorization.User!;

            // A book is opened, never played: no stream or segment of it reaches stock or a remote (D63).
            if (stubs.Values.Any(stub => stub.Kind == StubKind.Book))
            {
                context.Result = _refusal.RefuseBook(context.HttpContext, authorization.DeviceId ?? string.Empty);
                return;
            }

            foreach (var playSessionId in named)
            {
                _sessions.UseLocal(user.Id, playSessionId);
            }

            var executed = await next().ConfigureAwait(false);

            // Stock leaves the failed job behind with a request that never ends, and a retry would wait for it for good.
            if (executed.Exception is FfmpegException && !string.IsNullOrWhiteSpace(PlaySessionOf(context)))
            {
                await _transcodes.KillTranscodingJobs(authorization.DeviceId ?? string.Empty, PlaySessionOf(context), _ => true).ConfigureAwait(false);
            }

            if (executed.Exception is not null && !executed.ExceptionHandled && stubs.Values.Any(stub => _coordinator.TakeFileRefusal(stub.Id)))
            {
                executed.Exception = null;
                executed.ExceptionHandled = true;
                executed.Result = _refusal.Refuse(context.HttpContext, authorization.DeviceId ?? string.Empty, PlaybackRefusal.LimitText);
            }

            return;
        }

        await next().ConfigureAwait(false);
    }

    // Answers a refusal. A legacy file route that is not found reaches stock with a name no file has, so that it answers
    // with its own body for a missing file and a refused name tells no more than a name of nothing (rows 23 and 78).
    private static async Task RefuseAsync(ActionExecutingContext context, ActionExecutionDelegate next, IActionResult refusal, string controller, string action)
    {
        if (refusal is StatusCodeResult { StatusCode: StatusCodes.Status404NotFound } && IsLegacyFile(controller, action))
        {
            context.ActionArguments[action == LegacyAudioAction ? "segmentId" : "playlistId"] = MissingFilePrefix + Guid.NewGuid().ToString("N");
            await next().ConfigureAwait(false);
            return;
        }

        context.Result = refusal;
    }

    // The single check of a route that names stubs (row 458): 401 without a user, 404 when one of the stubs is not visible
    // to them, 403 on Download without the download permission (D12). Null when the route goes on.
    private IActionResult? AccessRefusal(AuthorizationInfo authorization, IEnumerable<Guid> stubs, StubAccess access)
    {
        if (authorization.User is not { } user)
        {
            return new StatusCodeResult(StatusCodes.Status401Unauthorized);
        }

        if (stubs.Any(id => !_stubs.Sees(user, id)))
        {
            return new StatusCodeResult(StatusCodes.Status404NotFound);
        }

        return access == StubAccess.Download && !user.HasPermission(PermissionKind.EnableContentDownloading)
            ? new StatusCodeResult(StatusCodes.Status403Forbidden)
            : null;
    }

    // Finds the first entry of the stub table for the action that covers a stub it names, with those stubs; null for none.
    private StubRoute? StubRouteOf(ActionExecutingContext context, string controller, string action, out List<Guid> covered)
    {
        covered = [];
        foreach (var route in _stubRoutes)
        {
            if (route.Controller != controller || (route.Action is not null && route.Action != action))
            {
                continue;
            }

            covered = StubLookup.NamedItemIdsOf(context).Where(id => route.Covers == StubCover.Audio ? _stubs.IsAudioStub(id) : _stubs.IsStub(id)).ToList();
            if (covered.Count > 0)
            {
                return route;
            }
        }

        return null;
    }

    // Serves Download and File of a stub its user may reach from its remote file; a file no source gives is a 503, with a
    // text for a book placeholder (931).
    private async Task<IActionResult> FileResultAsync(ActionExecutingContext context, AuthorizationInfo authorization, Guid itemId)
    {
        var action = StubLookup.ActionOf(context).Action;
        if (_stubs.FindFile(itemId) is not { } stub)
        {
            return Unavailable(itemId);
        }

        var http = context.HttpContext;
        var method = HttpMethods.IsHead(http.Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        var range = http.Request.Headers.Range.ToString();
        using var opened = await _coordinator.OpenFileAsync(stub, method, string.IsNullOrEmpty(range) ? null : range, http.RequestAborted).ConfigureAwait(false);
        if (opened is null)
        {
            return _coordinator.TakeFileRefusal(stub.Id)
                ? _refusal.Refuse(http, authorization.DeviceId ?? string.Empty, PlaybackRefusal.LimitText)
                : Unavailable(itemId);
        }

        // A book goes with the type of its format, never the remote's (D62), and as a file with its local name on Download.
        var mediaType = stub.Kind == StubKind.Book ? BookPlaceholders.MediaTypeOf(stub.RelativePath) : null;
        if (stub.Kind == StubKind.Book)
        {
            http.Response.Headers.XContentTypeOptions = "nosniff";
            if (action == DownloadAction)
            {
                var disposition = new ContentDispositionHeaderValue("attachment");
                disposition.SetHttpFileName(Path.GetFileName(stub.RelativePath));
                http.Response.Headers.ContentDisposition = disposition.ToString();
            }
        }

        await StreamRelay.CopyAsync(opened, http, _coordinator.StallLimit, http.RequestAborted, mediaType: mediaType).ConfigureAwait(false);
        return new EmptyResult();
    }

    // Serves the lyrics of a track stub its user may reach from its remote; they are read, never managed.
    private async Task<IActionResult> LyricsResultAsync(ActionExecutingContext context, AuthorizationInfo authorization, Guid track)
    {
        return StubLookup.ActionOf(context).Action != GetLyricsAction
            ? new StatusCodeResult(StatusCodes.Status403Forbidden)
            : _stubs.FindFile(track) is { } stub
                ? await _audio.LyricsAsync(context.HttpContext, authorization.DeviceId ?? string.Empty, stub).ConfigureAwait(false)
                : new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
    }

    // Serves a stock stream route on audio stubs their user may reach: a static stream is the file; the other audio routes
    // go through a session, anything else is a bare 503.
    private async Task<IActionResult> AudioStubResultAsync(ActionExecutingContext context, AuthorizationInfo authorization, List<Guid> tracks)
    {
        if (tracks is not [var track] || StubLookup.ItemIdOf(context) != track || _stubs.FindFile(track) is not { } stub || string.IsNullOrEmpty(authorization.Token))
        {
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
        }

        if (StubLookup.ActionOf(context).Controller == "Audio" && context.ActionArguments.TryGetValue("static", out var isStatic) && isStatic is true)
        {
            return await FileResultAsync(context, authorization, track).ConfigureAwait(false);
        }

        return await _audio.ServeAsync(context, new AudioAccess(authorization.User!.Id, authorization.DeviceId ?? string.Empty, authorization.Token, track), stub).ConfigureAwait(false);
    }

    // A 503, with a text for a book placeholder, whose readers show it.
    private IActionResult Unavailable(Guid itemId)
    {
        return _stubs.IsPlaceholder(itemId)
            ? new ContentResult { StatusCode = StatusCodes.Status503ServiceUnavailable, Content = PlaybackRefusal.FileOfflineText, ContentType = "text/plain; charset=utf-8" }
            : new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
    }

    // Tells whether a play session is recorded here, of a stub played on this server or of a peer. No peer is anonymous.
    private bool IsRecorded(string? playSessionId)
    {
        return _sessions.LocalItemOf(playSessionId) is not null || _peerSlots.IsOfAnother(Guid.Empty, playSessionId);
    }

    // Refuses a stream that names the play session of a peer, as one that names the local play session of another user.
    // Null when the peer itself names it, or when no peer recorded it.
    private async Task<IActionResult?> PeerSessionRefusalAsync(ActionExecutingContext context, IReadOnlyList<string> named)
    {
        var ofPeers = named.Where(id => _peerSlots.IsOfAnother(Guid.Empty, id)).ToList();
        if (ofPeers.Count == 0)
        {
            return null;
        }

        var user = (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
        if (user is null)
        {
            return new StatusCodeResult(StatusCodes.Status401Unauthorized);
        }

        return ofPeers.Exists(id => _peerSlots.IsOfAnother(user.Id, id)) ? new StatusCodeResult(StatusCodes.Status404NotFound) : null;
    }

    // The play sessions a media request names: its own, or for a legacy route, which names none, every recorded play
    // session whose transcode job its files belong to.
    private IReadOnlyList<string> MediaPlaySessionsOf(ActionExecutingContext context, string controller, string action)
    {
        if (!IsLegacyFile(controller, action))
        {
            return PlaySessionOf(context) is { } playSessionId ? [playSessionId] : [];
        }

        var playlistId = ArgumentOf(context, "playlistId");
        var segmentId = ArgumentOf(context, "segmentId");
        if (action != LegacySegmentAction)
        {
            // Stock reads the file the name and the extension of the path make, and looks for nothing else.
            var name = (action == LegacyPlaylistAction ? playlistId : segmentId) + Path.GetExtension(context.HttpContext.Request.Path.Value);
            (playlistId, segmentId) = (null, name);
        }

        // Nobody is anonymous, so the play sessions of others than nobody are all of them. Each folder is listed once.
        var listings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        return _sessions.LocalSessionsOfOthers(Guid.Empty)
            .Concat(_peerSlots.SessionsOfOthers(Guid.Empty))
            .Where(id => _transcodes.GetTranscodingJob(id)?.Path is { } path && IsFileOfJob(path, playlistId, segmentId, listings))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Stock serves the segment file by name, and pings the job of the first file whose path holds the playlist id. The
    // files of a job start with the name of its playlist.
    private bool IsFileOfJob(string jobPath, string? playlistId, string? segmentId, Dictionary<string, IReadOnlyList<string>> listings)
    {
        var prefix = Path.GetFileNameWithoutExtension(jobPath);
        return (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(segmentId) && Path.GetFileName(segmentId).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(playlistId) && FilesOfJob(jobPath, prefix, listings).Any(path => path.Contains(playlistId, StringComparison.OrdinalIgnoreCase)));
    }

    // The path of the job and of the files of its folder that start with the name of its playlist, such as its segments.
    // A folder already in the listings is not listed again.
    private IEnumerable<string> FilesOfJob(string jobPath, string prefix, Dictionary<string, IReadOnlyList<string>> listings)
    {
        yield return jobPath;
        var folder = Path.GetDirectoryName(jobPath);
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(folder))
        {
            yield break;
        }

        if (!listings.TryGetValue(folder, out var files))
        {
            files = _listFolder(folder);
            listings[folder] = files;
        }

        foreach (var file in files.Where(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal)))
        {
            yield return file;
        }
    }

    /// <summary>
    /// Lists the files of a folder, none when it is missing or cannot be read.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The paths of its files.</returns>
    internal static IReadOnlyList<string> ListFolder(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder).ToList() : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Q54: when an item the action names has linked versions, its user must see every item it names. A Playstate report
    // gets 204 without effect, as for the play session of another user (D8). Null when the action goes on.
    private async Task<IActionResult?> VersionRefusalAsync(ActionExecutingContext context, string controller, string action, IReadOnlyList<string> sessions)
    {
        var report = IsVersionReport(controller, action);
        if (!report && !IsMedia(controller, action))
        {
            return null;
        }

        var named = StubLookup.NamedItemIdsOf(context).ToList();
        if (!report)
        {
            named.AddRange(sessions.Select(_sessions.LocalItemOf).OfType<Guid>());
        }

        if (!named.Exists(_stubs.HasVersions))
        {
            return null;
        }

        if ((await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User is not { } user)
        {
            return new StatusCodeResult(StatusCodes.Status401Unauthorized);
        }

        if (named.TrueForAll(id => _stubs.Sees(user, id)))
        {
            return null;
        }

        return report ? new NoContentResult() : new StatusCodeResult(StatusCodes.Status404NotFound);
    }

    // Tells whether one of the play sessions has a live stock job without a device.
    private bool HasJobWithoutDevice(IEnumerable<string> playSessionIds)
    {
        return playSessionIds.Any(id => _transcodes.GetTranscodingJob(id) is { } job && string.IsNullOrWhiteSpace(job.DeviceId));
    }

    // Finds every stub the action names: as the entity, as the version, or through the local play session of a stub. The
    // play session counts even when its stub no longer resolves. Guarded adds the named stubs of any kind the registry lacks.
    private Dictionary<Guid, StubEntry> StubsOf(ActionExecutingContext context, IReadOnlyList<string> sessions, out bool recorded, out HashSet<Guid> guarded)
    {
        var stubs = new Dictionary<Guid, StubEntry>();
        var named = StubLookup.NamedItemIdsOf(context).ToList();
        var played = sessions.Select(_sessions.LocalItemOf).OfType<Guid>().ToList();
        recorded = played.Count > 0;
        named.AddRange(played);

        guarded = [];
        foreach (var id in named)
        {
            if (_stubs.Find(id) is { } stub)
            {
                stubs[id] = stub;
                guarded.Add(id);
            }
            else if (_stubs.IsStub(id))
            {
                guarded.Add(id);
            }
        }

        return stubs;
    }

    private static bool IsMedia(string controller, string action)
    {
        return _mediaControllers.Contains(controller) || IsFileAction(controller, action) || IsLegacyFile(controller, action);
    }

    // The legacy routes that serve a file of the transcode folder by name; the stop route of their controller is apart.
    private static bool IsLegacyFile(string controller, string action)
    {
        return controller == "HlsSegment" && action is LegacySegmentAction or LegacyAudioAction or LegacyPlaylistAction;
    }

    // The legacy segment routes, anonymous on stock; the playlist route wants a user already.
    private static bool IsAnonymousLegacy(string controller, string action)
    {
        return controller == "HlsSegment" && action is LegacySegmentAction or LegacyAudioAction;
    }

    private static string? ArgumentOf(ActionExecutingContext context, string name)
    {
        return context.ActionArguments.TryGetValue(name, out var value) ? value as string : null;
    }

    private static bool IsFileAction(string controller, string action)
    {
        return controller == "Library" && action is DownloadAction or GetFileAction;
    }

    // Only a segment makes stock stop the jobs of a device; playlists and live.m3u8 stop none.
    private static bool IsSegment(string controller, string action)
    {
        return controller == "DynamicHls" && action is "GetHlsVideoSegment" or "GetHlsAudioSegment";
    }

    private static bool IsStop(string controller, string action)
    {
        return (controller == "HlsSegment" && action == StopEncodingAction)
            || (controller == "Playstate" && action is "ReportPlaybackStopped" or "OnPlaybackStopped" or "OnPlaybackStoppedLegacy");
    }

    // A start, a progress or a ping of its user keeps a local play session in use; one of another user gets 204.
    private static bool IsReport(string controller, string action)
    {
        return controller == "Playstate"
            && action is "ReportPlaybackStart" or "OnPlaybackStart" or "OnPlaybackStartLegacy"
                or "ReportPlaybackProgress" or "OnPlaybackProgress" or "OnPlaybackProgressLegacy" or "PingPlaybackSession";
    }

    // A Playstate start, progress or stop, which can name a version; a ping names none.
    private static bool IsVersionReport(string controller, string action)
    {
        return controller == "Playstate" && (IsStop(controller, action) || (IsReport(controller, action) && action != "PingPlaybackSession"));
    }

    private static string? DeviceOf(ActionExecutingContext context)
    {
        return StubLookup.LegacyParamOf(context, StubLookup.LegacyDeviceField)
            ?? (context.ActionArguments.TryGetValue("deviceId", out var value) ? value as string : null);
    }

    private static string? PlaySessionOf(ActionExecutingContext context)
    {
        if (context.ActionArguments.TryGetValue("playbackStopInfo", out var report))
        {
            return (report as PlaybackStopInfo)?.PlaySessionId;
        }

        foreach (var name in new[] { "playbackProgressInfo", "playbackStartInfo" })
        {
            if (context.ActionArguments.TryGetValue(name, out var progress))
            {
                return (progress as PlaybackProgressInfo)?.PlaySessionId;
            }
        }

        return StubLookup.LegacyParamOf(context, StubLookup.LegacyPlaySessionField)
            ?? (context.ActionArguments.TryGetValue("playSessionId", out var value) ? value as string : null);
    }
}
