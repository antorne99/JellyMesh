using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class PeerGuardFilterTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly PeerGuardFilter _filter;

    public PeerGuardFilterTests()
    {
        _filter = new PeerGuardFilter(_fixture.Directory, _fixture.Catalog, _fixture.Slots);
    }

    private static string Route(Guid id) => id.ToString("N");

    private async Task<(ResourceExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, string method, Guid? item, string query = "", Action? during = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.QueryString = new QueryString(query);
        var route = new RouteData();
        if (item is { } id)
        {
            route.Values["itemId"] = Route(id);
        }

        var context = new ResourceExecutingContext(new ActionContext(http, route, action), new List<IFilterMetadata>(), new List<IValueProviderFactory>());
        var ran = false;
        await _filter.OnResourceExecutionAsync(context, () =>
        {
            ran = true;
            during?.Invoke();
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        });
        return (context, ran);
    }

    private static int? StatusOf(ResourceExecutingContext context) => (context.Result as StatusCodeResult)?.StatusCode;

    [Fact]
    public async Task Anyone_who_is_not_a_peer_passes_untouched()
    {
        _fixture.Caller = new Jellyfin.Database.Implementations.Entities.User("mario", "auth", "reset");

        var (context, ran) = await RunAsync(ProviderFixture.Stock("Items", "GetItems"), "GET", null);

        Assert.True(ran);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task An_anonymous_request_passes_as_it_does_on_stock_jellyfin()
    {
        _fixture.Caller = null;

        var (_, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", ProviderFixture.PrivateItem);

        Assert.True(ran);
    }

    [Fact]
    public async Task With_no_peers_the_request_is_not_even_looked_at()
    {
        _fixture.Store.Configuration.Peers.Clear();

        var (_, ran) = await RunAsync(ProviderFixture.Stock("Items", "GetItems"), "GET", null);

        Assert.True(ran);
        _fixture.Authorization.Verify(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()), Times.Never);
    }

    [Theory]
    [InlineData("Items", "GetItems")]
    [InlineData("UserViews", "GetUserViews")]
    [InlineData("Library", "GetFile")]
    [InlineData("Library", "GetDownload")]
    [InlineData("Search", "GetSearchHints")]
    [InlineData("Configuration", "GetNamedConfiguration")]
    [InlineData("Unknown", "AddedByAFutureJellyfin")]
    public async Task Stock_routes_are_closed_to_a_peer(string controller, string action)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
    }

    [Theory]
    [InlineData("POST", "Image", "PostItemImage")]
    [InlineData("DELETE", "Image", "DeleteItemImage")]
    [InlineData("DELETE", "Subtitle", "DeleteSubtitle")]
    public async Task A_peer_can_only_read_on_item_routes(string method, string controller, string action)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), method, ProviderFixture.SharedItem);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
    }

    [Theory]
    [InlineData("User", "GetCurrentUser")]
    [InlineData("User", "AuthenticateUserByName")]
    [InlineData("System", "GetPublicSystemInfo")]
    public async Task What_a_consumer_needs_stays_open(string controller, string action)
    {
        var (_, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", null);

        Assert.True(ran);
    }

    [Fact]
    public async Task A_peer_can_close_its_transcode()
    {
        var (_, ran) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null);

        Assert.True(ran);
    }

    [Fact]
    public async Task Closing_a_transcode_takes_no_slot_even_on_a_full_server()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "holding")!.Dispose();

        var (_, ran) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null);

        Assert.True(ran);
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task The_close_route_opens_for_delete_only()
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "GET", null);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
    }

    [Fact]
    public async Task The_provider_api_is_open_to_a_peer()
    {
        var (_, ran) = await RunAsync(ProviderFixture.Plugin<ProviderController>("GetItems"), "GET", null);

        Assert.True(ran);
    }

    [Theory]
    [InlineData("Image", "GetItemImage", null)]
    [InlineData("Subtitle", "GetSubtitle", null)]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", null)]
    [InlineData("Videos", "GetVideoStream", null)]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", "?PlaySessionId=play")]
    public async Task A_stub_does_not_exist_for_a_peer(string controller, string action, string? query)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.StubItem, query ?? string.Empty);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Theory]
    [InlineData("Image", "GetItemImage", null)]
    [InlineData("Subtitle", "GetSubtitle", null)]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", null)]
    [InlineData("Videos", "GetVideoStream", null)]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", "?PlaySessionId=play")]
    public async Task An_item_outside_the_share_does_not_exist_for_a_peer(string controller, string action, string? query)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.PrivateItem, query ?? string.Empty);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task An_item_route_needs_an_item()
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock("Image", "GetItemImage"), "GET", null);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task A_shared_item_is_served()
    {
        var (_, ran) = await RunAsync(ProviderFixture.Stock("Image", "GetItemImage"), "GET", ProviderFixture.SharedItem);

        Assert.True(ran);
    }

    [Fact]
    public async Task A_stream_holds_its_slot_while_it_is_written()
    {
        var during = -1;

        await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", ProviderFixture.SharedItem, string.Empty, () => during = _fixture.Slots.Count(_fixture.Peer.Id));

        Assert.Equal(1, during);
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task A_stream_over_the_limit_is_refused_with_the_signal()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "other");

        var (context, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", ProviderFixture.SharedItem, "?PlaySessionId=play");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(context));
        Assert.Equal(ProviderProtocol.StreamLimitRefusal, context.HttpContext.Response.Headers[ProviderProtocol.RefusalHeader].ToString());
    }

    [Fact]
    public async Task A_segment_of_the_session_passes_at_the_limit()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "play");

        var (_, ran) = await RunAsync(ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment"), "GET", ProviderFixture.SharedItem, "?PlaySessionId=play");

        Assert.True(ran);
    }

    [Fact]
    public async Task Hls_without_a_play_session_is_refused()
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock("DynamicHls", "GetMasterHlsVideoPlaylist"), "GET", ProviderFixture.SharedItem);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
    }

    [Fact]
    public async Task An_image_needs_no_slot()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "other");

        var (_, ran) = await RunAsync(ProviderFixture.Stock("Image", "GetItemImage"), "GET", ProviderFixture.SharedItem);

        Assert.True(ran);
    }
}

public class PeerPlaybackInfoFilterTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly PeerPlaybackInfoFilter _filter;

    public PeerPlaybackInfoFilterTests()
    {
        _filter = new PeerPlaybackInfoFilter(_fixture.Directory, _fixture.Slots);
    }

    private Task<(ActionExecutingContext Context, bool Ran)> RunAsync(IActionResult? result, string? replaces = null, Exception? failure = null)
    {
        return RunAsync(ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"), result, replaces, failure);
    }

    private async Task<(ActionExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, IActionResult? result, string? replaces = null, Exception? failure = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        if (replaces is not null)
        {
            http.Request.Headers[ProviderProtocol.ReplacesHeader] = replaces;
        }

        var actionContext = new ActionContext(http, new RouteData(), action);
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
        var ran = false;
        await _filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            if (failure is not null)
            {
                throw failure;
            }

            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()) { Result = result });
        });
        return (context, ran);
    }

    private static OkObjectResult Answer(string? playSession = "play", PlaybackErrorCode? error = null)
    {
        return new OkObjectResult(new PlaybackInfoResponse { PlaySessionId = playSession, ErrorCode = error });
    }

    [Fact]
    public async Task A_playback_holds_a_slot_named_after_its_play_session()
    {
        await RunAsync(Answer());

        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));

        // The stream that follows uses the same slot, so a limit of 1 lets it through.
        using var stream = _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "play");
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task Over_the_limit_it_is_refused_before_anything_runs()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "other");

        var (context, ran) = await RunAsync(Answer());

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (context.Result as StatusCodeResult)?.StatusCode);
        Assert.Equal(ProviderProtocol.StreamLimitRefusal, context.HttpContext.Response.Headers[ProviderProtocol.RefusalHeader].ToString());
    }

    [Fact]
    public async Task An_answer_with_an_error_holds_no_slot()
    {
        await RunAsync(Answer(error: PlaybackErrorCode.NoCompatibleStream));

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task An_answer_without_a_play_session_holds_no_slot()
    {
        await RunAsync(Answer(playSession: null));

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task An_action_that_fails_holds_no_slot()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(null, failure: new InvalidOperationException()));

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task A_replacement_passes_on_a_full_server()
    {
        await RunAsync(Answer("old"));

        var (_, ran) = await RunAsync(Answer("new"), "old");

        Assert.True(ran);
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task Anyone_else_takes_no_slot()
    {
        _fixture.Caller = new Jellyfin.Database.Implementations.Entities.User("mario", "auth", "reset");

        var (_, ran) = await RunAsync(Answer());

        Assert.True(ran);
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task Another_action_takes_no_slot()
    {
        var (_, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), Answer());

        Assert.True(ran);
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }
}
