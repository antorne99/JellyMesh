using System;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.Extensions.Primitives;
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

    // What Jellyfin makes of a query sent as one encoded key: keys that differ by case, read by stock's binder as they are.
    private static Dictionary<string, StringValues> Decoded(params (string Key, string Value)[] pairs)
    {
        return pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value), StringComparer.Ordinal);
    }

    // The legacy params query parameter with the play session in field 20, the one stock reads over PlaySessionId.
    private static string LegacyParams(string playSession) => "params=" + Uri.EscapeDataString(new string(';', 20)) + playSession;

    private async Task<(ResourceExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, string method, Guid? item, string query = "", Action? during = null, Action<DefaultHttpContext>? ends = null, Exception? failure = null, Dictionary<string, StringValues>? decoded = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.QueryString = new QueryString(query);
        if (decoded is not null)
        {
            http.Request.Query = new QueryCollection(decoded);
        }

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
        await _fixture.Closer.CloseAbandonedAsync();
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

    [Theory]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist")]
    [InlineData("DynamicHls", "GetVariantHlsVideoPlaylist")]
    [InlineData("Videos", "GetVideoStream")]
    public async Task A_stream_cannot_hide_a_play_session_of_someone_else_in_the_legacy_params(string controller, string action)
    {
        _fixture.Own("play");
        _fixture.Own("theirs", Guid.NewGuid());

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem, "?PlaySessionId=play&" + LegacyParams("theirs"));

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
        Assert.Equal(0, Slots);
    }

    [Theory]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    [InlineData("Videos", "GetVideoStream")]
    public async Task A_play_session_in_the_legacy_params_is_the_one_of_the_stream(string controller, string action)
    {
        _fixture.Own("play");
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "play");

        var (_, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem, "?" + LegacyParams("play"));

        Assert.True(ran);
    }

    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    public async Task A_blank_play_session_in_the_legacy_params_leaves_the_query_in_charge(string blank)
    {
        _fixture.Own("play");
        _fixture.Own("theirs", Guid.NewGuid());
        var segment = ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment");

        var (_, own) = await RunAsync(segment, "GET", ProviderFixture.SharedItem, "?PlaySessionId=play&" + LegacyParams(blank) + "%3Bx");
        var (refused, theirs) = await RunAsync(segment, "GET", ProviderFixture.SharedItem, "?PlaySessionId=theirs&" + LegacyParams(blank) + "%3Bx");

        Assert.True(own);
        Assert.False(theirs);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refused));
    }

    [Fact]
    public async Task Only_the_first_legacy_params_counts_as_on_stock()
    {
        _fixture.Own("play");
        _fixture.Own("theirs", Guid.NewGuid());
        var segment = ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment");

        var (first, firstRan) = await RunAsync(segment, "GET", ProviderFixture.SharedItem, "?PlaySessionId=play&" + LegacyParams("theirs") + "&params=x");
        var (second, secondRan) = await RunAsync(segment, "GET", ProviderFixture.SharedItem, "?PlaySessionId=theirs&params=x&" + LegacyParams("play"));

        Assert.False(firstRan);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(first));
        Assert.False(secondRan);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(second));
    }

    [Fact]
    public async Task A_failed_start_of_ffmpeg_clears_the_play_session_of_the_legacy_params()
    {
        _fixture.Own("play");

        await RunAsync(ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment"), "GET", ProviderFixture.SharedItem, "?" + LegacyParams("play"), failure: new FfmpegException("exited"));

        VerifyClosed("play", Times.Once());
    }

    [Fact]
    public async Task A_stream_reads_the_play_session_under_the_name_stock_binds_in_a_decoded_query()
    {
        _fixture.Own("play");
        _fixture.Own("theirs", Guid.NewGuid());
        var segment = ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment");

        var (refused, theirsRan) = await RunAsync(segment, "GET", ProviderFixture.SharedItem, decoded: Decoded(("playSessionId", "theirs"), ("PlaySessionId", "play")));
        var (_, ownRan) = await RunAsync(segment, "GET", ProviderFixture.SharedItem, decoded: Decoded(("playSessionId", "play"), ("PlaySessionId", "theirs")));

        Assert.False(theirsRan);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refused));
        Assert.True(ownRan);
    }

    [Fact]
    public async Task A_stream_reads_the_legacy_params_under_the_name_stock_binds_in_a_decoded_query()
    {
        _fixture.Own("play");
        _fixture.Own("theirs", Guid.NewGuid());
        var twenty = new string(';', 20);

        var (context, ran) = await RunAsync(ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment"), "GET", ProviderFixture.SharedItem, decoded: Decoded(("playSessionId", "play"), ("Params", twenty + "play"), ("params", twenty + "theirs")));

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
    }

    [Fact]
    public async Task The_close_route_reads_the_play_session_under_the_name_stock_binds_in_a_decoded_query()
    {
        _fixture.Own("play");
        _fixture.Own("theirs", Guid.NewGuid());
        var close = ProviderFixture.Stock("HlsSegment", "StopEncodingProcess");

        var (refused, theirsRan) = await RunAsync(close, "DELETE", null, decoded: Decoded(("deviceId", "d"), ("playSessionId", "theirs"), ("PlaySessionId", "play")));
        var (_, ownRan) = await RunAsync(close, "DELETE", null, decoded: Decoded(("deviceId", "d"), ("playSessionId", "play"), ("PlaySessionId", "theirs")));

        Assert.False(theirsRan);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refused));
        Assert.True(ownRan);
    }

    [Fact]
    public async Task The_close_route_reads_only_the_query_as_stock_does()
    {
        _fixture.Own("play");
        _fixture.Own("theirs", Guid.NewGuid());
        var close = ProviderFixture.Stock("HlsSegment", "StopEncodingProcess");

        var (refused, theirsRan) = await RunAsync(close, "DELETE", null, "?deviceId=d&playSessionId=theirs&" + LegacyParams("play"));
        var (_, ownRan) = await RunAsync(close, "DELETE", null, "?deviceId=d&playSessionId=play&" + LegacyParams("theirs"));

        Assert.False(theirsRan);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refused));
        Assert.True(ownRan);
    }

    [Fact]
    public async Task A_paused_stream_resumes_after_its_slot_expired()
    {
        _fixture.Own("play");

        var (_, ran) = await StreamAsync(during: () => Assert.Equal(1, Slots));

        Assert.True(ran);
    }

    [Fact]
    public async Task A_lost_connection_frees_the_slot_after_the_grace_once_its_transcode_is_closed()
    {
        _fixture.Own("play");

        await StreamAsync(ends: ConnectionLost);

        Elapse(StreamSlots.AbandonGrace - TimeSpan.FromSeconds(1));
        await _fixture.Closer.CloseAbandonedAsync();
        Assert.Equal(1, Slots);
        Elapse(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Slots);
        await _fixture.Closer.CloseAbandonedAsync();
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
        Assert.Equal(1, Slots);

        await StreamAsync();
        await _fixture.Closer.CloseAbandonedAsync();

        VerifyNothingClosed();
    }

    [Fact]
    public async Task A_slot_that_expires_at_the_idle_time_closes_its_transcode_with_nobody_asking()
    {
        _fixture.Own("play");
        await StreamAsync();

        Elapse(StreamSlots.IdleTimeout);
        await _fixture.Closer.CloseAbandonedAsync();
        await _fixture.Closer.CloseAbandonedAsync();

        Assert.Equal(0, Slots);
        VerifyClosed("play", Times.Once());
    }

    [Fact]
    public async Task With_the_setting_off_an_expired_slot_leaves_the_job_to_stock()
    {
        _fixture.Store.Configuration.CloseTranscodeAtSlotExpiry = false;
        _fixture.Own("play");
        await StreamAsync();

        Elapse(StreamSlots.IdleTimeout);
        await _fixture.Closer.CloseAbandonedAsync();

        Assert.Equal(0, Slots);
        VerifyNothingClosed();
    }

    [Fact]
    public async Task With_the_setting_off_a_real_drop_still_closes_the_job()
    {
        _fixture.Store.Configuration.CloseTranscodeAtSlotExpiry = false;
        _fixture.Own("play");
        await StreamAsync(ends: ConnectionLost);

        Elapse(StreamSlots.AbandonGrace);
        await _fixture.Closer.CloseAbandonedAsync();

        VerifyClosed("play", Times.Once());
    }

    [Fact]
    public async Task The_transcode_stays_until_the_idle_time()
    {
        _fixture.Own("play");
        await StreamAsync();

        Elapse(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));
        await _fixture.Closer.CloseAbandonedAsync();

        VerifyNothingClosed();
    }

    [Fact]
    public async Task A_stream_being_read_at_the_idle_time_keeps_its_transcode()
    {
        _fixture.Own("play");

        await StreamAsync(during: () =>
        {
            Elapse(StreamSlots.IdleTimeout * 2);
            _fixture.Closer.CloseAbandonedAsync().GetAwaiter().GetResult();
            VerifyNothingClosed();
        });
    }

    [Fact]
    public async Task A_consumer_back_after_the_idle_time_gets_a_slot_and_goes_on()
    {
        _fixture.Own("play");
        await StreamAsync();
        Elapse(StreamSlots.IdleTimeout);
        await _fixture.Closer.CloseAbandonedAsync();

        var during = -1;
        var (_, ran) = await StreamAsync(during: () => during = Slots);

        Assert.True(ran);
        Assert.Equal(1, during);
    }

    [Fact]
    public async Task The_next_stream_starts_only_after_an_expired_transcode_closed()
    {
        _fixture.Own("gone");
        _fixture.Own("next");
        await StreamAsync("gone");
        Elapse(StreamSlots.IdleTimeout);

        var (_, ran) = await StreamAsync("next", during: () => VerifyClosed("gone", Times.Once()));

        Assert.True(ran);
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
    public async Task A_stream_that_comes_while_the_timer_closes_the_abandoned_transcode_waits_for_it()
    {
        _fixture.Own("gone");
        _fixture.Own("next");
        await StreamAsync("gone", ends: ConnectionLost);
        Elapse(StreamSlots.AbandonGrace);
        var killing = new TaskCompletionSource();
        _fixture.Transcodes.Setup(manager => manager.KillTranscodingJobs(It.IsAny<string>(), "gone", It.IsAny<Func<string, bool>>())).Returns(killing.Task);
        var timer = _fixture.Closer.CloseAbandonedAsync();

        var (context, ran) = await StreamAsync("next");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(context));

        killing.SetResult();
        await timer;
        var (_, ranAfter) = await StreamAsync("next");

        Assert.True(ranAfter);
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
        await _fixture.Closer.CloseAbandonedAsync();
        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_stream_that_throws_frees_the_slot_after_the_grace()
    {
        _fixture.Own("play");

        await StreamAsync(failure: new InvalidOperationException());

        Elapse(StreamSlots.AbandonGrace);
        await _fixture.Closer.CloseAbandonedAsync();
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

    [Theory]
    [InlineData("Videos", "GetVideoStream")]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    public async Task A_stream_naming_a_live_stream_in_the_query_is_refused_on_its_own_play_session(string controller, string action)
    {
        _fixture.Own("play");
        var stream = ProviderFixture.Stock(controller, action);

        var (refused, liveRan) = await RunAsync(stream, "GET", ProviderFixture.SharedItem, "?playSessionId=play&liveStreamId=0123456789abcdef0123456789abcdef");
        var slotsAfterRefusal = Slots;
        var (_, ownRan) = await RunAsync(stream, "GET", ProviderFixture.SharedItem, "?playSessionId=play");

        Assert.False(liveRan);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refused));
        Assert.False(refused.HttpContext.Response.Headers.ContainsKey(ProviderProtocol.RefusalHeader));
        Assert.Equal(0, slotsAfterRefusal);
        Assert.True(ownRan);
    }

    // A legacy params with the live stream in field 22, the one stock reads over LiveStreamId.
    private static string LiveStreamField(string liveStream) => "params=" + Uri.EscapeDataString(new string(';', 22) + liveStream);

    [Theory]
    [InlineData("Videos", "GetVideoStream")]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    public async Task A_stream_naming_a_live_stream_in_the_legacy_params_is_refused(string controller, string action)
    {
        _fixture.Own("play");

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem, "?playSessionId=play&" + LiveStreamField("0123456789abcdef0123456789abcdef"));

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
        Assert.Equal(0, Slots);
    }

    [Theory]
    [InlineData("&liveStreamId=live&params=%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%20")]
    [InlineData("&params=%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3Blive&params=x")]
    [InlineData("&liveStreamId=&liveStreamId=live")]
    public async Task A_stream_is_refused_wherever_stock_may_read_a_live_stream(string query)
    {
        _fixture.Own("play");

        var (context, ran) = await RunAsync(ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment"), "GET", ProviderFixture.SharedItem, "?playSessionId=play" + query);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(context));
    }

    [Theory]
    [InlineData("&liveStreamId=%20")]
    [InlineData("&params=%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%20")]
    [InlineData("&params=x&params=%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3B%3Blive")]
    public async Task A_stream_whose_live_stream_stock_skips_passes(string query)
    {
        _fixture.Own("play");

        var (_, ran) = await RunAsync(ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment"), "GET", ProviderFixture.SharedItem, "?playSessionId=play" + query);

        Assert.True(ran);
    }

    [Fact]
    public async Task A_stream_reads_the_live_stream_under_the_name_stock_binds_in_a_decoded_query()
    {
        _fixture.Own("play");
        var segment = ProviderFixture.Stock("DynamicHls", "GetHlsVideoSegment");

        var (refused, boundRan) = await RunAsync(segment, "GET", ProviderFixture.SharedItem, decoded: Decoded(("playSessionId", "play"), ("liveStreamId", "live")));
        var (_, unboundRan) = await RunAsync(segment, "GET", ProviderFixture.SharedItem, decoded: Decoded(("playSessionId", "play"), ("LiveStreamId", "live")));

        Assert.False(boundRan);
        Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refused));
        Assert.True(unboundRan);
    }
}

public class PeerPlaybackInfoFilterTests
{
    private readonly ProviderFixture _fixture = new();
    private readonly PeerPlaybackInfoFilter _filter;

    public PeerPlaybackInfoFilterTests()
    {
        _filter = new PeerPlaybackInfoFilter(_fixture.Directory, _fixture.Slots, _fixture.Closer);
    }

    private Task<(ActionExecutingContext Context, bool Ran)> RunAsync(IActionResult? result, string? replaces = null, Exception? failure = null, bool consumerLeft = false, Dictionary<string, object?>? arguments = null, bool bodyBound = true)
    {
        // Stock binds the posted body of every consumer PlaybackInfo, so the tests post one unless told otherwise.
        arguments ??= new Dictionary<string, object?>();
        if (bodyBound)
        {
            arguments.TryAdd("playbackInfoDto", new PostedBody());
        }

        return RunAsync(ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"), result, replaces, failure, consumerLeft, arguments);
    }

    private async Task<(ActionExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, IActionResult? result, string? replaces = null, Exception? failure = null, bool consumerLeft = false, Dictionary<string, object?>? arguments = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        if (replaces is not null)
        {
            http.Request.Headers[ProviderProtocol.ReplacesHeader] = replaces;
        }

        var actionContext = new ActionContext(http, new RouteData(), action);
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments ?? new Dictionary<string, object?>(), new object());
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
    public async Task At_the_limit_a_playback_after_the_grace_closes_the_abandoned_transcode_and_takes_its_slot()
    {
        var peer = _fixture.Peer.Id;
        _fixture.Slots.Confirm(peer, _fixture.Slots.TryReserve(peer, 1, null)!, "gone");
        var request = _fixture.Slots.TryEnter(peer, 1, "gone")!;
        request.Abandon();
        request.Dispose();
        _fixture.Clock.Advance(StreamSlots.AbandonGrace);

        var (_, ran) = await RunAsync(Answer("next"));

        Assert.True(ran);
        _fixture.Transcodes.Verify(manager => manager.KillTranscodingJobs(It.IsAny<string>(), "gone", It.IsAny<Func<string, bool>>()), Times.Once());
        Assert.Equal(1, _fixture.Slots.Count(peer));
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

    private static OkObjectResult AnswerWithTranscode(string transcodingUrl)
    {
        var answer = new PlaybackInfoResponse { PlaySessionId = "play" };
        answer.MediaSources = [new MediaBrowser.Model.Dto.MediaSourceInfo { TranscodingUrl = transcodingUrl }];
        return new OkObjectResult(answer);
    }

    [Fact]
    public async Task A_playback_records_the_device_of_the_peer_and_the_one_its_transcoding_url_names()
    {
        _fixture.CallerDevice = "consumer-device";

        await RunAsync(AnswerWithTranscode("/videos/x/master.m3u8?DeviceId=Url+Device%2B&DeviceId=second&PlaySessionId=play"));

        var other = Guid.NewGuid();
        Assert.True(_fixture.Slots.IsDeviceOfAnother(other, "CONSUMER-DEVICE"));
        Assert.True(_fixture.Slots.IsDeviceOfAnother(other, "url device+"));
        Assert.False(_fixture.Slots.IsDeviceOfAnother(other, "second"));
        Assert.False(_fixture.Slots.IsDeviceOfAnother(_fixture.Peer.Id, "consumer-device"));
    }

    [Fact]
    public async Task A_consumer_that_left_during_the_call_records_no_device()
    {
        _fixture.CallerDevice = "consumer-device";

        await RunAsync(AnswerWithTranscode("/videos/x/master.m3u8?DeviceId=url-device"), consumerLeft: true);

        Assert.False(_fixture.Slots.IsDeviceOfAnother(Guid.NewGuid(), "consumer-device"));
        Assert.False(_fixture.Slots.IsDeviceOfAnother(Guid.NewGuid(), "url-device"));
    }

    // The posted body as stock binds it: PlaybackInfoDto lives in Jellyfin.Api, which is not a NuGet package.
    private sealed class PostedBody
    {
        public string? LiveStreamId { get; init; }
    }

    [Theory]
    [InlineData("liveStreamId")]
    [InlineData("playbackInfoDto")]
    public async Task A_playback_naming_a_live_stream_is_refused_before_it_takes_a_slot(string where)
    {
        await RunAsync(Answer("old"));
        var arguments = new Dictionary<string, object?>
        {
            [where] = where == "liveStreamId" ? "0123456789abcdef0123456789abcdef" : new PostedBody { LiveStreamId = "0123456789abcdef0123456789abcdef" }
        };

        var (context, ran) = await RunAsync(Answer("new"), "old", arguments: arguments);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, (context.Result as StatusCodeResult)?.StatusCode);
        Assert.False(context.HttpContext.Response.Headers.ContainsKey(ProviderProtocol.RefusalHeader));
        Assert.True(_fixture.Slots.Owns(_fixture.Peer.Id, "old"));
        Assert.False(_fixture.Slots.Owns(_fixture.Peer.Id, "new"));
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    // A body whose live stream has another name or type: a later Jellyfin that changed it, so the check cannot see it.
    private sealed class RenamedBody
    {
        public string? LiveStream { get; init; }

        public int LiveStreamId { get; init; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_playback_whose_body_cannot_be_checked_for_a_live_stream_is_refused(bool bodyBound)
    {
        var arguments = new Dictionary<string, object?>();
        if (bodyBound)
        {
            arguments["playbackInfoDto"] = new RenamedBody { LiveStream = "0123456789abcdef0123456789abcdef" };
        }

        var (context, ran) = await RunAsync(Answer(), arguments: arguments, bodyBound: false);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status403Forbidden, (context.Result as StatusCodeResult)?.StatusCode);
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task A_playback_with_a_null_body_passes()
    {
        var (_, ran) = await RunAsync(Answer(), arguments: new Dictionary<string, object?> { ["playbackInfoDto"] = null });

        Assert.True(ran);
        Assert.True(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
    }

    [Fact]
    public async Task A_playback_whose_live_stream_is_blank_passes()
    {
        var arguments = new Dictionary<string, object?> { ["liveStreamId"] = " ", ["playbackInfoDto"] = new PostedBody { LiveStreamId = string.Empty } };

        var (_, ran) = await RunAsync(Answer(), arguments: arguments);

        Assert.True(ran);
        Assert.True(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
    }
}
