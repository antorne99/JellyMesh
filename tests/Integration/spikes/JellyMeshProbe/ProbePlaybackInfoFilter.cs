using System.Globalization;
using System.Security.Claims;
using MediaBrowser.Controller.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellyMesh.Probe;

/// <summary>
/// Mode "filter": answers PlaybackInfo for stubs before the controller runs, with a
/// response shaped like the one a remote would return. Mode "filter-after" lets the
/// controller run and edits its result. Registered as a global MVC filter, so it
/// sees the authenticated user and the bound DeviceProfile.
/// </summary>
public sealed class ProbePlaybackInfoFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<ProbePlaybackInfoFilter> _logger;

    public ProbePlaybackInfoFilter(ILibraryManager libraryManager, IUserManager userManager, ILogger<ProbePlaybackInfoFilter> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var item = FindStub(context);
        if (item is null)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // PlaybackInfoDto lives in Jellyfin.Api, which is not a NuGet package.
        context.ActionArguments.TryGetValue("playbackInfoDto", out var dto);
        var profile = dto?.GetType().GetProperty("DeviceProfile")?.GetValue(dto) as DeviceProfile;
        _logger.LogInformation(
            "JMPROBE filter: {Path} profile={HasProfile} directPlayProfiles={Count} user={User}",
            item.Path,
            profile is not null,
            profile?.DirectPlayProfiles.Length,
            context.HttpContext.User.Identity?.Name);

        var id = item.Id.ToString("N", CultureInfo.InvariantCulture);
        if (Catalog.Has("filter"))
        {
            var source = Catalog.Source(id, "/JellyMesh/Play/" + id, Catalog.Has("external"));
            source.IsRemote = false;
            source.SupportsDirectPlay = false;
            source.SupportsDirectStream = false;
            source.TranscodingUrl = "/JellyMesh/Hls/" + id + "/master.m3u8";
            source.TranscodingSubProtocol = MediaStreamProtocol.hls;
            source.TranscodingContainer = "ts";
            context.Result = new OkObjectResult(new PlaybackInfoResponse
            {
                MediaSources = [source],
                PlaySessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            });
            return;
        }

        var executed = await next().ConfigureAwait(false);
        if (executed.Result is ObjectResult { Value: PlaybackInfoResponse response })
        {
            foreach (var source in response.MediaSources)
            {
                source.Path = "/JellyMesh/Play/" + id;

                // Mode "filter-ds": no raw Path for the client, the server proxies the .strm.
                if (Catalog.Has("filter-ds"))
                {
                    source.SupportsDirectPlay = false;
                    source.SupportsDirectStream = true;
                    source.TranscodingUrl = null;
                }

                // Mode "filter-file": looks local, so clients build the static URL themselves.
                if (Catalog.Has("filter-file"))
                {
                    source.Protocol = MediaProtocol.File;
                    source.IsRemote = false;
                    source.SupportsDirectPlay = true;
                    source.SupportsDirectStream = true;
                    source.TranscodingUrl = null;
                }
            }
        }
    }

    private BaseItem? FindStub(ActionExecutingContext context)
    {
        if (!(Catalog.Has("filter") || Catalog.Has("filter-after"))
            || context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "MediaInfo" } action
            || !action.ActionName.Contains("PlaybackInfo", StringComparison.Ordinal)
            || !context.ActionArguments.TryGetValue("itemId", out var value)
            || value is not Guid itemId)
        {
            return null;
        }

        var name = context.HttpContext.User.FindFirstValue(ClaimTypes.Name);
        var user = name is null ? null : _userManager.GetUserByName(name);
        var item = user is null ? null : _libraryManager.GetItemById<BaseItem>(itemId, user);
        return item is not null && Catalog.IsFederated(item) ? item : null;
    }
}
