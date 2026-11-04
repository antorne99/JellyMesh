using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class PlaybackFiltersTests
{
    private const string Token = PlaybackValues.LocalToken;
    private static readonly Guid _itemId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly CoordinatorFixture _fixture = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly StubEntry _stub;

    public PlaybackFiltersTests()
    {
        _stub = PlaybackValues.Stub(PlaybackValues.Source());
        var video = new Movie { ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature") };
        _library.Setup(library => library.GetItemById(_itemId)).Returns(video);
        _stubs.Setup(stubs => stubs.Find("stub")).Returns(_stub);
        SignedIn();
    }

    private void SignedIn()
    {
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = _user, Token = Token, DeviceId = "device" });
    }

    private void Anonymous()
    {
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo());
    }

    private void CanSeeItem()
    {
        _library.Setup(library => library.GetItemById<BaseItem>(_itemId, _user)).Returns(new Movie());
    }

    private PlaybackInfoFilter InfoFilter()
    {
        return new PlaybackInfoFilter(
            new StubLookup(_library.Object, _stubs.Object),
            _library.Object,
            _authorization.Object,
            _fixture.Coordinator,
            new PlaybackRefusal(_sessionManager.Object, NullLogger<PlaybackRefusal>.Instance));
    }

    private StockRouteGuardFilter GuardFilter()
    {
        return new StockRouteGuardFilter(
            new StubLookup(_library.Object, _stubs.Object),
            _library.Object,
            _authorization.Object,
            _fixture.Coordinator,
            _fixture.Sessions);
    }

    private static ActionExecutingContext Context(string controller, string action, params (string Key, object? Value)[] arguments)
    {
        var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action };
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor);
        var values = new Dictionary<string, object?>();
        foreach (var (key, value) in arguments)
        {
            values[key] = value;
        }

        return new ActionExecutingContext(actionContext, [], values!, new object());
    }

    private static async Task<bool> RunAsync(IAsyncActionFilter filter, ActionExecutingContext context)
    {
        var passed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            passed = true;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });
        return passed;
    }

    [Theory]
    [InlineData("GetPlaybackInfo")]
    [InlineData("GetPostedPlaybackInfo")]
    public async Task Both_variants_answer_for_a_stub_with_a_plugin_url(string action)
    {
        CanSeeItem();
        var context = Context("MediaInfo", action, ("itemId", _itemId));

        var passed = await RunAsync(InfoFilter(), context);

        Assert.False(passed);
        var response = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(context.Result).Value);
        Assert.StartsWith("/JellyMesh/Playback/", response.MediaSources[0].TranscodingUrl, StringComparison.Ordinal);
        Assert.Contains("ApiKey=" + Token, response.MediaSources[0].TranscodingUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anything_that_is_not_a_stub_goes_to_the_stock_action()
    {
        var other = Guid.NewGuid();
        _library.Setup(library => library.GetItemById(other)).Returns(new Movie());

        Assert.True(await RunAsync(InfoFilter(), Context("MediaInfo", "GetPlaybackInfo", ("itemId", other))));
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task Without_a_user_nothing_reaches_a_remote()
    {
        Anonymous();
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));

        await RunAsync(InfoFilter(), context);

        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task Without_access_to_the_library_it_is_not_found_and_nothing_reaches_a_remote()
    {
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));

        await RunAsync(InfoFilter(), context);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Requests);
    }

    [Fact]
    public async Task With_every_server_offline_the_answer_is_a_bare_503()
    {
        CanSeeItem();
        _fixture.Registry.Offline.Add(_stub.Sources[0].ServerId);
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));

        await RunAsync(InfoFilter(), context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_local_transcode_needs_a_profile()
    {
        CanSeeItem();
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));

        var passed = await RunAsync(InfoFilter(), context);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("Videos", "GetVideoStream", "itemId")]
    [InlineData("Videos", "HeadVideoStream", "itemId")]
    [InlineData("Audio", "GetAudioStream", "itemId")]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId")]
    [InlineData("VideoAttachments", "GetAttachment", "itemId")]
    [InlineData("Library", "GetDownload", "itemId")]
    public async Task A_stock_media_route_refuses_a_stub_without_a_user(string controller, string action, string argument)
    {
        Anonymous();
        var context = Context(controller, action, (argument, _itemId));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stock_media_route_checks_the_library_of_the_stub()
    {
        var context = Context("Videos", "GetVideoStream", ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stock_media_route_serves_a_user_who_can_see_the_stub()
    {
        CanSeeItem();

        Assert.True(await RunAsync(GuardFilter(), Context("DynamicHls", "GetMasterHlsVideoPlaylist", ("itemId", _itemId))));
    }

    [Fact]
    public async Task A_stock_media_route_is_untouched_for_other_items()
    {
        Anonymous();
        var other = Guid.NewGuid();
        _library.Setup(library => library.GetItemById(other)).Returns(new Movie());

        Assert.True(await RunAsync(GuardFilter(), Context("Videos", "GetVideoStream", ("itemId", other))));
    }

    [Fact]
    public async Task Other_controllers_are_untouched()
    {
        Anonymous();

        Assert.True(await RunAsync(GuardFilter(), Context("Items", "GetItem", ("itemId", _itemId))));
    }

    private async Task<PlaybackSession> OpenSessionAsync()
    {
        await _fixture.OpenAsync(_stub);
        return Assert.Single(_fixture.Sessions.All);
    }

    [Fact]
    public async Task Delete_active_encodings_closes_the_session_of_its_user()
    {
        var identity = PlaybackValues.Identity() with { UserId = _user.Id };
        var session = new PlaybackSession(identity, _stub.Sources[0], "remote-ps", PlaybackStreamKind.Hls, "/m", _fixture.Clock.GetUtcNow());
        _fixture.Sessions.Add(session);

        var passed = await RunAsync(GuardFilter(), Context("HlsSegment", "StopEncodingProcess", ("playSessionId", session.Id)));

        Assert.True(passed);
        Assert.Null(_fixture.Sessions.Find(session.Id));
        Assert.Equal("remote-ps", Assert.Single(_fixture.Client.Stopped));
    }

    [Fact]
    public async Task Delete_active_encodings_leaves_the_session_of_another_user()
    {
        var session = await OpenSessionAsync();

        await RunAsync(GuardFilter(), Context("HlsSegment", "StopEncodingProcess", ("playSessionId", session.Id)));

        Assert.NotNull(_fixture.Sessions.Find(session.Id));
        Assert.Empty(_fixture.Client.Stopped);
    }
}
