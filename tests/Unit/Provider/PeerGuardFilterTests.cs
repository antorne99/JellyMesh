using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Common;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
        _filter = new PeerGuardFilter(_fixture.Directory, _fixture.Catalog, _fixture.Slots, _fixture.Closer);
    }

    private static string Route(Guid id) => id.ToString("N");

    private async Task<(ResourceExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, string method, Guid? item, string query = "", Action? during = null, Action<DefaultHttpContext>? ends = null, Exception? failure = null)
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
            ends?.Invoke(http);
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()) { Exception = failure });
        });
        return (context, ran);
    }

    private Task<(ResourceExecutingContext Context, bool Ran)> StreamAsync(string playSession = "play", Action? during = null, Action<DefaultHttpContext>? ends = null, Exception? failure = null)
    {
        return RunAsync(ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment"), "GET", ProviderFixture.SharedItem, "?PlaySessionId=" + playSession, during, ends, failure);
    }

    private static void ConnectionLost(DefaultHttpContext http)
    {
        http.RequestAborted = new CancellationToken(true);
    }

    private void Elapse(TimeSpan time) => _fixture.Clock.Advance(time);

    private int Slots => _fixture.Slots.Count(_fixture.Peer.Id);

    private void VerifyClosed(string playSession, Times times)
    {
        _fixture.Transcodes.Verify(manager => manager.KillTranscodingJobs(It.IsAny<string>(), playSession, It.IsAny<Func<string, bool>>()), times);
    }

    private void VerifyNothingClosed()
    {
        _fixture.Transcodes.Verify(manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()), Times.Never);
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
        _fixture.Own("play");

        var (_, ran) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null, "?deviceId=device&playSessionId=play");

        Assert.True(ran);
    }

    [Fact]
    public async Task A_transcode_can_be_closed_long_after_its_slot_expired()
    {
        var reservation = _fixture.Slots.TryReserve(_fixture.Peer.Id, 1, null)!;
        _fixture.Slots.Confirm(_fixture.Peer.Id, reservation, "play");
        Elapse(StreamSlots.IdleTimeout + TimeSpan.FromHours(24));
        Assert.Equal(0, Slots);

        var (_, ran) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null, "?deviceId=device&playSessionId=play");

        Assert.True(ran);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?deviceId=victim")]
    [InlineData("?deviceId=victim&playSessionId=")]
    [InlineData("?deviceId=victim&playSessionId=%20")]
    [InlineData("?deviceId=victim&playSessionId=someone-else")]
    [InlineData("?deviceId=victim&playSessionId=play&playSessionId=someone-else")]
    [InlineData("?deviceId=victim&playSessionId=someone-else&playSessionId=play")]
    public async Task A_peer_stops_nothing_it_did_not_open(string query)
    {
        _fixture.Own("play");

        var (context, ran) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null, query);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
    }

    [Fact]
    public async Task A_session_of_another_peer_and_an_unknown_one_get_the_same_answer()
    {
        _fixture.Own("theirs", Guid.NewGuid());

        var (known, _) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null, "?deviceId=d&playSessionId=theirs");
        var (unknown, _) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null, "?deviceId=d&playSessionId=nobody");

        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(known));
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(unknown));
    }

    [Fact]
    public async Task A_refused_close_takes_nothing_from_the_real_owner()
    {
        var other = Guid.NewGuid();
        _fixture.Own("theirs", other);

        await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null, "?deviceId=d&playSessionId=theirs");

        Assert.True(_fixture.Slots.Owns(other, "theirs"));
    }

    [Theory]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist")]
    [InlineData("DynamicHls", "GetVariantHlsVideoPlaylist")]
    [InlineData("Videos", "GetVideoStream")]
    public async Task A_stream_cannot_name_a_play_session_of_someone_else(string controller, string action)
    {
        _fixture.Own("theirs", Guid.NewGuid());

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem, "?PlaySessionId=theirs");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_paused_stream_resumes_after_its_slot_expired()
    {
        _fixture.Own("play");

        var (_, ran) = await StreamAsync(during: () => Assert.Equal(1, Slots));

        Assert.True(ran);
    }

    [Fact]
    public async Task A_lost_connection_frees_the_slot_after_the_grace()
    {
        _fixture.Own("play");

        await StreamAsync(ends: ConnectionLost);

        Elapse(StreamSlots.AbandonGrace - TimeSpan.FromSeconds(1));
        Assert.Equal(1, Slots);
        Elapse(TimeSpan.FromSeconds(1));
        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_seek_keeps_its_slot_at_the_limit()
    {
        _fixture.Own("play");
        await StreamAsync(ends: ConnectionLost);

        // Another stream asks in the gap between the two connections.
        Elapse(TimeSpan.FromMilliseconds(50));
        Assert.Null(_fixture.Slots.TryReserve(_fixture.Peer.Id, 1, null));
        var (_, ran) = await StreamAsync();
        Elapse(StreamSlots.AbandonGrace);

        Assert.True(ran);
        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task The_end_of_the_grace_closes_the_transcode_with_nobody_asking()
    {
        _fixture.Own("play");
        await StreamAsync(ends: ConnectionLost);

        Elapse(StreamSlots.AbandonGrace);
        await _fixture.Closer.CloseAbandonedAsync();
        await _fixture.Closer.CloseAbandonedAsync();

        VerifyClosed("play", Times.Once());
    }

    [Fact]
    public async Task The_transcode_stays_during_the_grace()
    {
        _fixture.Own("play");
        await StreamAsync(ends: ConnectionLost);

        Elapse(StreamSlots.AbandonGrace - TimeSpan.FromSeconds(1));
        await _fixture.Closer.CloseAbandonedAsync();

        VerifyNothingClosed();
    }

    [Fact]
    public async Task A_seek_within_the_grace_keeps_its_transcode()
    {
        _fixture.Own("play");
        await StreamAsync(ends: ConnectionLost);

        await StreamAsync();
        Elapse(StreamSlots.AbandonGrace);
        await _fixture.Closer.CloseAbandonedAsync();

        VerifyNothingClosed();
    }

    [Fact]
    public async Task The_next_stream_starts_only_after_the_last_transcode_closed()
    {
        _fixture.Own("gone");
        _fixture.Own("next");
        await StreamAsync("gone", ends: ConnectionLost);
        Elapse(StreamSlots.AbandonGrace);

        var (_, ran) = await StreamAsync("next", during: () => VerifyClosed("gone", Times.Once()));

        Assert.True(ran);
    }

    [Fact]
    public async Task A_refused_stream_closes_nothing_during_the_grace()
    {
        _fixture.Own("gone");
        _fixture.Own("next");
        await StreamAsync("gone", ends: ConnectionLost);

        var (context, ran) = await StreamAsync("next");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(context));
        VerifyNothingClosed();
    }

    [Fact]
    public async Task A_consumer_back_after_the_grace_gets_a_slot_and_goes_on()
    {
        _fixture.Own("play");
        await StreamAsync(ends: ConnectionLost);
        Elapse(StreamSlots.AbandonGrace);
        await _fixture.Closer.CloseAbandonedAsync();

        var during = -1;
        var (_, ran) = await StreamAsync(during: () => during = Slots);

        Assert.True(ran);
        Assert.Equal(1, during);
    }

    [Fact]
    public async Task A_consumer_back_after_the_grace_gets_the_refusal_of_the_limit_if_the_slot_went()
    {
        _fixture.Own("play");
        await StreamAsync(ends: ConnectionLost);
        Elapse(StreamSlots.AbandonGrace);
        await _fixture.Closer.CloseAbandonedAsync();
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "other");

        var (context, ran) = await StreamAsync();

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(context));
    }

    [Fact]
    public async Task A_session_that_came_back_as_the_grace_ended_keeps_its_transcode()
    {
        _fixture.Own("play");
        await StreamAsync(ends: ConnectionLost);
        Elapse(StreamSlots.AbandonGrace);
        Assert.Equal(0, Slots);

        await StreamAsync();
        await _fixture.Closer.CloseAbandonedAsync();

        VerifyNothingClosed();
    }

    [Fact]
    public async Task A_slot_that_expires_at_the_idle_time_closes_no_transcode()
    {
        _fixture.Own("play");
        await StreamAsync();

        Elapse(StreamSlots.IdleTimeout);
        await _fixture.Closer.CloseAbandonedAsync();

        Assert.Equal(0, Slots);
        VerifyNothingClosed();
    }

    [Fact]
    public async Task A_transcode_that_cannot_be_closed_stops_nothing_else()
    {
        _fixture.Own("gone");
        _fixture.Own("next");
        await StreamAsync("gone", ends: ConnectionLost);
        Elapse(StreamSlots.AbandonGrace);
        _fixture.Transcodes.Setup(manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>())).ThrowsAsync(new InvalidOperationException());

        var (_, ran) = await StreamAsync("next");

        Assert.True(ran);
    }

    [Fact]
    public async Task A_failed_start_of_ffmpeg_is_cleared_at_once_so_that_a_retry_does_not_wait_for_it()
    {
        _fixture.Own("play");

        await StreamAsync(failure: new FfmpegException("exited"));

        VerifyClosed("play", Times.Once());
    }

    [Fact]
    public async Task Another_failure_leaves_the_transcode_to_the_grace()
    {
        _fixture.Own("play");

        await StreamAsync(failure: new InvalidOperationException());

        VerifyNothingClosed();
    }

    [Fact]
    public async Task A_stream_can_be_cut_from_outside()
    {
        _fixture.Own("play");
        var lifetime = new Mock<IHttpRequestLifetimeFeature>();

        await StreamAsync(ends: http =>
        {
            http.Features.Set(lifetime.Object);
            _fixture.Slots.Revoke(_fixture.Peer.Id, _ => false);
        });

        lifetime.Verify(feature => feature.Abort(), Times.Once);
    }

    [Fact]
    public async Task A_stream_of_an_item_still_shared_is_not_cut()
    {
        _fixture.Own("play");
        var lifetime = new Mock<IHttpRequestLifetimeFeature>();

        await StreamAsync(ends: http =>
        {
            http.Features.Set(lifetime.Object);
            _fixture.Slots.Revoke(_fixture.Peer.Id, item => item == ProviderFixture.SharedItem);
        });

        lifetime.Verify(feature => feature.Abort(), Times.Never);
    }

    [Fact]
    public async Task A_stream_that_ends_normally_keeps_the_idle_time()
    {
        _fixture.Own("play");

        await StreamAsync();
        Elapse(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));

        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task A_lost_connection_leaves_a_slot_with_another_request_running()
    {
        _fixture.Own("play");
        using var running = _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "play");

        await StreamAsync(ends: ConnectionLost);
        Elapse(StreamSlots.IdleTimeout + StreamSlots.AbandonGrace);

        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task A_stream_that_fails_with_a_server_error_frees_the_slot_after_the_grace()
    {
        _fixture.Own("play");

        await StreamAsync(ends: http => http.Response.StatusCode = StatusCodes.Status500InternalServerError);

        Elapse(StreamSlots.AbandonGrace);
        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_stream_that_throws_frees_the_slot_after_the_grace()
    {
        _fixture.Own("play");

        await StreamAsync(failure: new InvalidOperationException());

        Elapse(StreamSlots.AbandonGrace);
        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_retry_after_a_server_error_keeps_the_slot()
    {
        _fixture.Own("play");
        await StreamAsync(ends: http => http.Response.StatusCode = StatusCodes.Status500InternalServerError);

        Elapse(StreamSlots.AbandonGrace - TimeSpan.FromSeconds(1));
        var (context, ran) = await StreamAsync();

        Assert.True(ran);
        Assert.Null(context.Result);
        Elapse(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task An_answer_short_of_a_server_error_is_no_failure()
    {
        _fixture.Own("play");

        await StreamAsync(ends: http => http.Response.StatusCode = StatusCodes.Status404NotFound);
        Elapse(StreamSlots.AbandonGrace);

        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task The_end_of_a_probe_is_not_a_client_that_left()
    {
        _fixture.Own("play");

        await StreamAsync(ends: http =>
        {
            http.Request.Headers[ProviderProtocol.ProbeHeader] = "1";
            ConnectionLost(http);
        });
        Elapse(StreamSlots.AbandonGrace + TimeSpan.FromSeconds(1));

        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task A_playback_info_whose_answer_never_left_frees_its_slot()
    {
        var reservation = _fixture.Slots.TryReserve(_fixture.Peer.Id, 1, null)!;

        await RunAsync(
            ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"),
            "POST",
            ProviderFixture.SharedItem,
            string.Empty,
            ends: http =>
            {
                _fixture.Slots.Confirm(_fixture.Peer.Id, reservation, "play");
                PeerGuardFilter.NotePlaybackInfo(http, "play");
                ConnectionLost(http);
            });

        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_playback_info_that_was_answered_keeps_its_slot()
    {
        var reservation = _fixture.Slots.TryReserve(_fixture.Peer.Id, 1, null)!;

        await RunAsync(
            ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"),
            "POST",
            ProviderFixture.SharedItem,
            string.Empty,
            ends: http =>
            {
                _fixture.Slots.Confirm(_fixture.Peer.Id, reservation, "play");
                PeerGuardFilter.NotePlaybackInfo(http, "play");
            });

        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task Closing_a_transcode_takes_no_slot_even_on_a_full_server()
    {
        _fixture.Own("play");
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "holding")!.Dispose();

        var (_, ran) = await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null, "?deviceId=device&playSessionId=play");

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
        _fixture.Own("play");
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "other");

        var (context, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", ProviderFixture.SharedItem, "?PlaySessionId=play");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(context));
        Assert.Equal(ProviderProtocol.StreamLimitRefusal, context.HttpContext.Response.Headers[ProviderProtocol.RefusalHeader].ToString());
    }

    [Fact]
    public async Task A_segment_of_the_session_passes_at_the_limit()
    {
        _fixture.Own("play");
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

    [Fact]
    public async Task A_peer_cannot_run_more_reads_than_the_limit_at_a_time()
    {
        var action = ProviderFixture.Plugin<ProviderController>(nameof(ProviderController.GetItems));
        var hold = new TaskCompletionSource();
        var running = new List<Task>();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";

        for (var read = 0; read < ProviderProtocol.MaxReadsPerPeer; read++)
        {
            var context = new ResourceExecutingContext(new ActionContext(http, new RouteData(), action), new List<IFilterMetadata>(), new List<IValueProviderFactory>());
            running.Add(_filter.OnResourceExecutionAsync(context, async () =>
            {
                await hold.Task;
                return new ResourceExecutedContext(context, new List<IFilterMetadata>());
            }));
        }

        var (refused, ran) = await RunAsync(action, "GET", null);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(refused));
        Assert.False(refused.HttpContext.Response.Headers.ContainsKey(ProviderProtocol.RefusalHeader));

        hold.SetResult();
        await Task.WhenAll(running);

        var (_, ranAfter) = await RunAsync(action, "GET", null);
        Assert.True(ranAfter);
    }

    [Fact]
    public async Task Renewing_a_slot_is_not_a_read_and_is_never_held_back()
    {
        var action = ProviderFixture.Plugin<ProviderController>(nameof(ProviderController.GetItems));
        var hold = new TaskCompletionSource();
        var running = new List<Task>();
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        for (var read = 0; read < ProviderProtocol.MaxReadsPerPeer; read++)
        {
            var context = new ResourceExecutingContext(new ActionContext(http, new RouteData(), action), new List<IFilterMetadata>(), new List<IValueProviderFactory>());
            running.Add(_filter.OnResourceExecutionAsync(context, async () =>
            {
                await hold.Task;
                return new ResourceExecutedContext(context, new List<IFilterMetadata>());
            }));
        }

        var (_, ran) = await RunAsync(ProviderFixture.Plugin<ProviderController>(nameof(ProviderController.RenewStream)), "PUT", null);

        Assert.True(ran);
        hold.SetResult();
        await Task.WhenAll(running);
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

    private Task<(ActionExecutingContext Context, bool Ran)> RunAsync(IActionResult? result, string? replaces = null, Exception? failure = null, bool consumerLeft = false)
    {
        return RunAsync(ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"), result, replaces, failure, consumerLeft);
    }

    private async Task<(ActionExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, IActionResult? result, string? replaces = null, Exception? failure = null, bool consumerLeft = false)
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

            if (consumerLeft)
            {
                http.RequestAborted = new CancellationToken(true);
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
    public async Task A_playback_makes_its_play_session_the_peers()
    {
        await RunAsync(Answer());

        Assert.True(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
        Assert.False(_fixture.Slots.Owns(Guid.NewGuid(), "play"));
    }

    [Fact]
    public async Task A_consumer_that_left_during_the_call_leaves_no_slot()
    {
        await RunAsync(Answer(), consumerLeft: true);

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task A_consumer_that_left_during_the_call_owns_nothing()
    {
        await RunAsync(Answer(), consumerLeft: true);

        Assert.False(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
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
