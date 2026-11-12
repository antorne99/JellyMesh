using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    private readonly StubLookup _stubs;
    private readonly IAuthorizationContext _authorization;
    private readonly PlaybackCoordinator _coordinator;
    private readonly PlaybackSessionStore _sessions;
    private readonly PlaybackRefusal _refusal;
    private readonly ITranscodeManager _transcodes;
    private readonly StreamSlots _peerSlots;

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
    public StockRouteGuardFilter(
        StubLookup stubs,
        IAuthorizationContext authorization,
        PlaybackCoordinator coordinator,
        PlaybackSessionStore sessions,
        PlaybackRefusal refusal,
        ITranscodeManager transcodes,
        StreamSlots peerSlots)
    {
        _stubs = stubs;
        _authorization = authorization;
        _coordinator = coordinator;
        _sessions = sessions;
        _refusal = refusal;
        _transcodes = transcodes;
        _peerSlots = peerSlots;
    }

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
        else if (IsMedia(controller, action)
            && StubsOf(context, named, out var recorded) is var stubs
            && (stubs.Count > 0 || recorded))
        {
            var authorization = await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
            if (authorization.User is not { } user)
            {
                context.Result = new StatusCodeResult(StatusCodes.Status401Unauthorized);
                return;
            }

            // A segment that restarts ffmpeg stops the job of the play session it names, as the stop route does.
            if (stubs.Keys.Any(id => !_stubs.Sees(user, id)) || named.Any(id => _sessions.IsLocalOfAnother(user.Id, id)))
            {
                await RefuseAsync(context, next, new StatusCodeResult(StatusCodes.Status404NotFound), controller, action).ConfigureAwait(false);
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

        // Nobody is anonymous, so the play sessions of others than nobody are all of them.
        return _sessions.LocalSessionsOfOthers(Guid.Empty)
            .Concat(_peerSlots.SessionsOfOthers(Guid.Empty))
            .Where(id => _transcodes.GetTranscodingJob(id)?.Path is { } path && IsFileOfJob(path, playlistId, segmentId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Stock serves the segment file by name, and pings the job of the first file whose path holds the playlist id. The
    // files of a job start with the name of its playlist.
    private static bool IsFileOfJob(string jobPath, string? playlistId, string? segmentId)
    {
        var prefix = Path.GetFileNameWithoutExtension(jobPath);
        return (!string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(segmentId) && Path.GetFileName(segmentId).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(playlistId) && FilesOfJob(jobPath, prefix).Any(path => path.Contains(playlistId, StringComparison.OrdinalIgnoreCase)));
    }

    // The path of the job and of the files of its folder that start with the name of its playlist, such as its segments.
    private static IEnumerable<string> FilesOfJob(string jobPath, string prefix)
    {
        yield return jobPath;
        var folder = Path.GetDirectoryName(jobPath);
        if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, prefix + "*").ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
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
    // play session counts even when its stub no longer resolves.
    private Dictionary<Guid, StubEntry> StubsOf(ActionExecutingContext context, IReadOnlyList<string> sessions, out bool recorded)
    {
        var stubs = new Dictionary<Guid, StubEntry>();
        var named = StubLookup.NamedItemIdsOf(context).ToList();
        var played = sessions.Select(_sessions.LocalItemOf).OfType<Guid>().ToList();
        recorded = played.Count > 0;
        named.AddRange(played);

        foreach (var id in named)
        {
            if (_stubs.Find(id) is { } stub)
            {
                stubs[id] = stub;
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
        return controller == "Library" && action is "GetDownload" or "GetFile";
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
