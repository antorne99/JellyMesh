using System.Globalization;
using System.Security.Claims;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellyMesh.Probe;

/// <summary>
/// Point 6 switches. Two lists of tokens, rewritten by POST /JellyMesh/ProbeMode so a
/// browser test can change them between cases without a restart:
/// refuse: code=X, raw=X, status=N, empty, source=direct|file|hls, target=dead|hang,
/// msg=alert|toast|late, delay=seconds, after=N, smart, notranscode, guard. item: placeholder, virtual, name, overview, tag, status, playaccess.
/// </summary>
public static class Refusal
{
    /// <summary>The name a careless error message would reveal. Must never reach a client.</summary>
    public const string Sentinel = "remoto-segreto.tailnet-sentinel.ts.net";

    public const string RefuseFile = "/config/jmprobe.refuse";
    public const string ItemFile = "/config/jmprobe.item";
    public const string Clip = "/config/clip.webm";

    /// <summary>PlaybackInfo calls since the mode last changed, for after=N.</summary>
    public static int Calls;

    public static IReadOnlyList<string> Tokens(string file)
    {
        return File.Exists(file)
            ? File.ReadAllText(file).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : [];
    }

    public static string? Value(IReadOnlyList<string> tokens, string key)
    {
        return tokens.FirstOrDefault(t => t.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
    }

    /// <summary>Stands in for "remote offline, no backup source" in the federation index.</summary>
    public static bool IsOffline(BaseItem? item)
    {
        return item is not null && Catalog.IsFederated(item) && item.Path.Contains("Offline", StringComparison.Ordinal);
    }

    /// <summary>A source shaped like what a remote answers for the webm clip.</summary>
    public static MediaSourceInfo Source(string id, string path)
    {
        var source = Catalog.Source(id, path, false);
        source.Container = "webm";
        source.RunTimeTicks = 80_000_000;
        source.Bitrate = 400_000;
        source.MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "vp9", Width = 640, Height = 360, BitRate = 300_000, IsDefault = true, AverageFrameRate = 24, RealFrameRate = 24 },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "opus", Channels = 2, SampleRate = 48_000, BitRate = 64_000, IsDefault = true, Language = "ita" },
        ];
        return source;
    }
}

/// <summary>Global filter: answers PlaybackInfo for offline stubs and marks their DTOs.</summary>
public sealed class ProbeRefusalFilter : IAsyncActionFilter
{
    private const string Text = "Server non raggiungibile";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<ProbeRefusalFilter> _logger;

    public ProbeRefusalFilter(ILibraryManager libraryManager, IUserManager userManager, ISessionManager sessionManager, ILogger<ProbeRefusalFilter> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var refuse = Refusal.Tokens(Refusal.RefuseFile);
        if (refuse.Count > 0 && FindOffline(context) is { } item)
        {
            _logger.LogInformation("JMPROBE refusal: {Item} tokens={Tokens} user={User}", item.Name, string.Join('|', refuse), context.HttpContext.User.Identity?.Name);

            // after=N: the first N calls succeed, as when the remote drops right after answering.
            var id = item.Id.ToString("N", CultureInfo.InvariantCulture);
            if (Refusal.Value(refuse, "after") is { } after
                && Interlocked.Increment(ref Refusal.Calls) <= int.Parse(after, CultureInfo.InvariantCulture))
            {
                context.Result = Healthy(refuse, id, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                return;
            }

            // Default: sent before answering, so the client has it whatever it does with the
            // answer. "late" sends it after the answer, so its dialog lands on top.
            var kind = Refusal.Value(refuse, "msg");
            if (kind is "alert" or "toast")
            {
                await Notify(context.HttpContext.User, kind).ConfigureAwait(false);
            }
            else if (kind == "late")
            {
                var user = context.HttpContext.User;
                _ = Task.Delay(400).ContinueWith(_ => Notify(user, "alert"), TaskScheduler.Default);
            }

            if (Refusal.Value(refuse, "delay") is { } delay)
            {
                await Task.Delay(TimeSpan.FromSeconds(int.Parse(delay, CultureInfo.InvariantCulture))).ConfigureAwait(false);
            }

            // smart: 503 on a first attempt, ErrorCode on the client's own retry, the only
            // two answers that end in a message on their respective code paths.
            if (refuse.Contains("smart"))
            {
                refuse = [.. refuse, IsRetry(context) ? "code=NotAllowed" : "status=503"];
            }

            context.Result = Answer(refuse, item);
            return;
        }

        var executed = await next().ConfigureAwait(false);
        var flags = Refusal.Tokens(Refusal.ItemFile);
        if (flags.Count > 0)
        {
            MarkAll(executed.Result, flags);
        }
    }

    private bool IsRetry(ActionExecutingContext context)
    {
        // Sent as a query parameter or in the body, depending on the client.
        context.ActionArguments.TryGetValue("enableDirectPlay", out var query);
        context.ActionArguments.TryGetValue("playbackInfoDto", out var dto);
        var body = dto?.GetType().GetProperty("EnableDirectPlay")?.GetValue(dto);
        _logger.LogInformation("JMPROBE retry check: query={Query} body={Body}", query, body);
        return query is false || body is false;
    }

    private static IActionResult Answer(IReadOnlyList<string> refuse, BaseItem item)
    {
        var id = item.Id.ToString("N", CultureInfo.InvariantCulture);
        var session = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        if (Refusal.Value(refuse, "code") is { } code)
        {
            return new OkObjectResult(new PlaybackInfoResponse
            {
                MediaSources = [],
                PlaySessionId = session,
                ErrorCode = Enum.Parse<PlaybackErrorCode>(code),
            });
        }

        // A value outside the enum: not expressible with the public type, so raw JSON.
        if (Refusal.Value(refuse, "raw") is { } raw)
        {
            return new ContentResult
            {
                Content = "{\"MediaSources\":[],\"PlaySessionId\":\"" + session + "\",\"ErrorCode\":\"" + raw + "\"}",
                ContentType = "application/json",
                StatusCode = StatusCodes.Status200OK,
            };
        }

        if (Refusal.Value(refuse, "status") is { } status)
        {
            // The sentinel is here on purpose: does a client ever render the body?
            return new ContentResult
            {
                Content = "{\"Message\":\"" + Text + " (" + Refusal.Sentinel + ")\"}",
                ContentType = "application/json",
                StatusCode = int.Parse(status, CultureInfo.InvariantCulture),
            };
        }

        if (refuse.Contains("empty"))
        {
            return new OkObjectResult(new PlaybackInfoResponse { MediaSources = [], PlaySessionId = session });
        }

        return Healthy(refuse, id, session);
    }

    /// <summary>A successful PlaybackInfo: the failure comes later, when the stream is opened.</summary>
    private static IActionResult Healthy(IReadOnlyList<string> refuse, string id, string session)
    {
        var source = Refusal.Source(id, "http://127.0.0.1:8096/JellyMesh/Play/film-offline");
        source.SupportsTranscoding = !refuse.Contains("notranscode");
        switch (Refusal.Value(refuse, "source"))
        {
            case "file":
                source.Protocol = MediaProtocol.File;
                source.IsRemote = false;
                break;
            case "hls":
                source.IsRemote = false;
                source.SupportsDirectPlay = false;
                source.SupportsDirectStream = false;
                source.TranscodingUrl = "/JellyMesh/Hls/" + id + "/master.m3u8";

                // guard: jellyfin-web stops its transcode retry when the URL says copy is already off.
                if (refuse.Contains("guard"))
                {
                    source.TranscodingUrl += "?AllowVideoStreamCopy=false&AllowAudioStreamCopy=false";
                }

                source.TranscodingSubProtocol = MediaStreamProtocol.hls;
                source.TranscodingContainer = "ts";
                break;
        }

        return new OkObjectResult(new PlaybackInfoResponse { MediaSources = [source], PlaySessionId = session });
    }

    private Task Notify(ClaimsPrincipal user, string kind)
    {
        var arguments = new Dictionary<string, string> { ["Header"] = "JellyMesh", ["Text"] = Text };
        if (kind == "toast")
        {
            arguments["TimeoutMs"] = "8000";
        }

        var command = new GeneralCommand(arguments) { Name = GeneralCommandType.DisplayMessage };
        var device = user.FindFirstValue("Jellyfin-DeviceId") ?? string.Empty;
        return _sessionManager.SendMessageToUserDeviceSessions(device, SessionMessageType.GeneralCommand, command, CancellationToken.None);
    }

    private BaseItem? FindOffline(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "MediaInfo" } action
            || !action.ActionName.Contains("PlaybackInfo", StringComparison.Ordinal)
            || !context.ActionArguments.TryGetValue("itemId", out var value)
            || value is not Guid itemId)
        {
            return null;
        }

        // Same check the controller does: an item the user cannot reach is not found.
        var name = context.HttpContext.User.FindFirstValue(ClaimTypes.Name);
        var user = name is null ? null : _userManager.GetUserByName(name);
        var item = user is null ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
        return Refusal.IsOffline(item) ? item : null;
    }

    private void MarkAll(IActionResult? result, IReadOnlyList<string> flags)
    {
        switch ((result as ObjectResult)?.Value)
        {
            case BaseItemDto dto:
                Mark(dto, flags);
                break;
            case QueryResult<BaseItemDto> query:
                foreach (var dto in query.Items)
                {
                    Mark(dto, flags);
                }

                break;
            case IEnumerable<BaseItemDto> list:
                foreach (var dto in list)
                {
                    Mark(dto, flags);
                }

                break;
        }
    }

    private void Mark(BaseItemDto dto, IReadOnlyList<string> flags)
    {
        if (!Refusal.IsOffline(_libraryManager.GetItemById(dto.Id)))
        {
            return;
        }

        if (flags.Contains("placeholder"))
        {
            dto.IsPlaceHolder = true;
        }

        if (flags.Contains("virtual"))
        {
            dto.LocationType = LocationType.Virtual;
        }

        if (flags.Contains("name"))
        {
            dto.Name += " (non disponibile)";
        }

        if (flags.Contains("overview"))
        {
            dto.Overview = "Non disponibile: server non raggiungibile. " + dto.Overview;
        }

        if (flags.Contains("tag"))
        {
            dto.Tags = [.. dto.Tags ?? [], "Non disponibile"];
        }

        if (flags.Contains("status"))
        {
            dto.Status = "Non disponibile";
        }

        if (flags.Contains("playaccess"))
        {
            dto.PlayAccess = PlayAccess.None;
        }
    }
}

/// <summary>The endpoints a .strm or TranscodingUrl lands on, and the mode switch.</summary>
[ApiController]
[AllowAnonymous]
public sealed class ProbeRefusalController : ControllerBase
{
    [HttpGet("JellyMesh/Play/{name}")]
    [HttpHead("JellyMesh/Play/{name}")]
    public Task<IActionResult> Play([FromRoute] string name, CancellationToken cancellationToken)
    {
        return Stream(name, () => PhysicalFile(Refusal.Clip, "video/webm", enableRangeProcessing: true), cancellationToken);
    }

    [HttpGet("JellyMesh/Hls/{id}/master.m3u8")]
    public Task<IActionResult> Hls([FromRoute] string id, CancellationToken cancellationToken)
    {
        return Stream("offline", () => NotFound(), cancellationToken);
    }

    [HttpPost("JellyMesh/ProbeMode")]
    public ActionResult Mode([FromQuery] string? refuse, [FromQuery] string? item)
    {
        System.IO.File.WriteAllText(Refusal.RefuseFile, refuse ?? string.Empty, Encoding.UTF8);
        System.IO.File.WriteAllText(Refusal.ItemFile, item ?? string.Empty, Encoding.UTF8);
        Refusal.Calls = 0;
        return NoContent();
    }

    private async Task<IActionResult> Stream(string name, Func<IActionResult> live, CancellationToken cancellationToken)
    {
        var target = Refusal.Value(Refusal.Tokens(Refusal.RefuseFile), "target");
        if (name.Contains("offline", StringComparison.OrdinalIgnoreCase) && target == "hang")
        {
            await Task.Delay(TimeSpan.FromSeconds(600), cancellationToken).ConfigureAwait(false);
        }

        if (name.Contains("offline", StringComparison.OrdinalIgnoreCase) && target == "dead")
        {
            return new ContentResult
            {
                Content = "{\"Message\":\"Server non raggiungibile (" + Refusal.Sentinel + ")\"}",
                ContentType = "application/json",
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
        }

        return live();
    }
}
