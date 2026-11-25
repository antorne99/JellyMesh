using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
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
        _filter = new PeerGuardFilter(_fixture.Directory, _fixture.Catalog, _fixture.Slots, _fixture.Closer, _fixture.Database.Object);
        _fixture.WithAudioLibraries();
    }

    private static string Route(Guid id) => id.ToString("N");

    // What Jellyfin makes of a query sent as one encoded key: keys that differ by case, read by stock's binder as they are.
    private static Dictionary<string, StringValues> Decoded(params (string Key, string Value)[] pairs)
    {
        return pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value), StringComparer.Ordinal);
    }

    // The legacy params query parameter with the play session in field 20, the one stock reads over PlaySessionId.
    private static string LegacyParams(string playSession) => "params=" + Uri.EscapeDataString(new string(';', 20)) + playSession;

    private async Task<(ResourceExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, string method, Guid? item, string query = "", Action? during = null, Action<DefaultHttpContext>? ends = null, Exception? failure = null, Dictionary<string, StringValues>? decoded = null, string? playSession = null, StringValues device = default)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.QueryString = new QueryString(query);
        if (device.Count > 0)
        {
            http.Request.Headers[ProviderProtocol.DeviceHeader] = device;
        }

        if (decoded is not null)
        {
            http.Request.Query = new QueryCollection(decoded);
        }

        var route = new RouteData();
        if (item is { } id)
        {
            route.Values["itemId"] = Route(id);
        }

        if (playSession is not null)
        {
            route.Values["playSessionId"] = playSession;
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

    // Nothing outside the peers table marks a peer, so this filter lets everyone by; StoreNotLoadedFilter stops non-admins (D32).
    [Fact]
    public async Task While_the_peers_cannot_be_read_a_local_user_passes_untouched()
    {
        _fixture.Caller = new Jellyfin.Database.Implementations.Entities.User("mario", "auth", "reset");
        _fixture.Store.Unavailable = true;

        var (context, ran) = await RunAsync(ProviderFixture.Stock("Items", "GetItems"), "GET", null);

        Assert.True(ran);
        Assert.Null(context.Result);
    }

    // While the database cannot be used a peer has no access, and what it already plays goes on (row 524).
    [Theory]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", "POST")]
    [InlineData("Videos", "GetVideoStream", "GET")]
    [InlineData("DynamicHls", "GetHlsVideoSegment", "GET")]
    [InlineData("Subtitle", "GetSubtitle", "GET")]
    [InlineData("Image", "GetItemImage", "GET")]
    [InlineData("MediaSegments", "GetItemSegments", "GET")]
    [InlineData("User", "GetCurrentUser", "GET")]
    [InlineData("HlsSegment", "StopEncodingProcess", "DELETE")]
    [InlineData("Items", "GetItems", "GET")]
    public async Task With_the_database_damaged_a_peer_gets_503(string controller, string action, string method)
    {
        _fixture.Health = new StoreHealth(StoreState.Damaged, "test");

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), method, ProviderFixture.SharedItem, "?PlaySessionId=unknown");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData(nameof(ProviderController.GetLibraries), "GET")]
    [InlineData(nameof(ProviderController.GetChanges), "GET")]
    [InlineData(nameof(ProviderController.RenewStream), "PUT")]
    [InlineData(nameof(ProviderController.ReleaseStream), "DELETE")]
    public async Task With_the_database_damaged_the_provider_api_answers_503(string action, string method)
    {
        _fixture.Health = new StoreHealth(StoreState.Damaged, "test");

        var (context, ran) = await RunAsync(ProviderFixture.Plugin<ProviderController>(action), method, null);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task With_the_database_damaged_a_peer_goes_on_with_the_stream_it_plays()
    {
        var reservation = _fixture.Slots.TryReserve(_fixture.Peer.Id, 1, null)!;
        _fixture.Slots.Confirm(_fixture.Peer.Id, reservation, "play", ProviderFixture.SharedItem);
        _fixture.Health = new StoreHealth(StoreState.Damaged, "test");

        Assert.True((await StreamAsync()).Ran);
        Assert.True((await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", ProviderFixture.SharedItem, "?" + LegacyParams("play"))).Ran);
        Assert.True((await RunAsync(ProviderFixture.Stock("Subtitle", "GetSubtitle"), "GET", ProviderFixture.SharedItem)).Ran);
        Assert.True((await RunAsync(ProviderFixture.Plugin<ProviderController>(nameof(ProviderController.RenewStream)), "PUT", null, playSession: "play")).Ran);
        Assert.True((await RunAsync(ProviderFixture.Plugin<ProviderController>(nameof(ProviderController.ReleaseStream)), "DELETE", null, playSession: "play")).Ran);
        Assert.True((await RunAsync(ProviderFixture.Stock("HlsSegment", "StopEncodingProcess"), "DELETE", null, "?PlaySessionId=play")).Ran);
    }

    [Fact]
    public async Task With_the_database_damaged_the_stream_a_peer_plays_opens_no_other_title()
    {
        var reservation = _fixture.Slots.TryReserve(_fixture.Peer.Id, 1, null)!;
        _fixture.Slots.Confirm(_fixture.Peer.Id, reservation, "play", Guid.NewGuid());
        _fixture.Health = new StoreHealth(StoreState.Damaged, "test");

        var (context, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", ProviderFixture.SharedItem, "?playSessionId=play");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task With_the_database_damaged_anyone_who_is_not_a_peer_passes_untouched()
    {
        _fixture.Health = new StoreHealth(StoreState.Damaged, "test");
        _fixture.Caller = new Jellyfin.Database.Implementations.Entities.User("mario", "auth", "reset");

        Assert.True((await RunAsync(ProviderFixture.Stock("Items", "GetItems"), "GET", null)).Ran);
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
    [InlineData("Library", "GetItemCounts")]
    [InlineData("UniversalAudio", "GetUniversalAudioStream")]
    [InlineData("Lyrics", "SearchRemoteLyrics")]
    [InlineData("Lyrics", "GetRemoteLyrics")]
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
    [InlineData("DynamicHls", "GetHlsAudioSegment")]
    [InlineData("DynamicHls", "GetMasterHlsAudioPlaylist")]
    [InlineData("Videos", "GetVideoStream")]
    [InlineData("Audio", "GetAudioStream")]
    [InlineData("Audio", "GetAudioStreamByContainer")]
    [InlineData("Library", "GetFile")]
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
    [InlineData("DynamicHls", "GetVariantHlsAudioPlaylist")]
    [InlineData("Videos", "GetVideoStream")]
    [InlineData("Audio", "GetAudioStream")]
    [InlineData("Audio", "GetAudioStreamByContainer")]
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
    [InlineData("Audio", "GetAudioStream")]
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
    public async Task The_next_stream_starts_only_after_a_revoked_transcode_closed()
    {
        _fixture.Own("lost");
        _fixture.Own("next");
        await StreamAsync("lost");
        _fixture.Slots.Revoke(_fixture.Peer.Id, _ => false);

        var (_, ran) = await StreamAsync("next", during: () => VerifyClosed("lost", Times.Once()));

        Assert.True(ran);
    }

    [Fact]
    public async Task A_revoked_stream_back_while_its_transcode_is_being_closed_gets_the_refusal_of_the_limit()
    {
        _fixture.Connect(2);
        _fixture.Own("play");
        await StreamAsync();
        var killing = new TaskCompletionSource();
        _fixture.Transcodes.Setup(manager => manager.KillTranscodingJobs(It.IsAny<string>(), "play", It.IsAny<Func<string, bool>>())).Returns(killing.Task);
        _fixture.Slots.Revoke(_fixture.Peer.Id, _ => false);
        var timer = _fixture.Closer.CloseAbandonedAsync();

        var (context, ran) = await StreamAsync();

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(context));
        Assert.Equal(ProviderProtocol.StreamLimitRefusal, context.HttpContext.Response.Headers[ProviderProtocol.RefusalHeader].ToString());
        Assert.Equal(1, Slots);

        killing.SetResult();
        await timer;
        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_stream_cut_by_a_lower_limit_is_refused_while_its_transcode_is_being_closed()
    {
        _fixture.Connect(2);
        _fixture.Own("oldest");
        _fixture.Own("newest");
        await StreamAsync("oldest");
        await StreamAsync("newest");
        var killing = new TaskCompletionSource();
        _fixture.Transcodes.Setup(manager => manager.KillTranscodingJobs(It.IsAny<string>(), "newest", It.IsAny<Func<string, bool>>())).Returns(killing.Task);
        _fixture.Slots.CutToLimit(_fixture.Peer.Id, 1);
        var timer = _fixture.Closer.CloseAbandonedAsync();

        var (context, ran) = await StreamAsync("newest");

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusOf(context));
        Assert.Equal(2, Slots);

        killing.SetResult();
        await timer;
        Assert.Equal(1, Slots);
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

    private const string DeviceOne = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string DeviceTwo = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private async Task<bool> FileAsync(StringValues device, Guid? item = null, string query = "?static=true")
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", item ?? ProviderFixture.SharedTrack, query, device: device);
        Assert.Equal(ran ? null : StatusCodes.Status429TooManyRequests, StatusOf(context));
        return ran;
    }

    [Fact]
    public async Task A_track_read_with_no_play_session_holds_the_slot_of_its_device_until_the_idle_time()
    {
        Assert.True(await FileAsync(DeviceOne));
        Assert.Equal(1, Slots);

        Assert.True(await FileAsync(DeviceOne));
        Assert.False(await FileAsync(DeviceTwo));
        Assert.False(await FileAsync(default));

        Elapse(StreamSlots.IdleTimeout);
        Assert.True(await FileAsync(DeviceTwo));
        VerifyNothingClosed();
    }

    // Row 561: an audiobook is audio, so its reads share the slot of their device as those of a track.
    [Fact]
    public async Task An_audiobook_read_holds_the_slot_of_its_device()
    {
        var book = Guid.Parse("61000000-0000-0000-0000-000000000000");
        _fixture.AddItem(book, new MediaBrowser.Controller.Entities.AudioBook { Path = "/media/books/book.m4b" }, ProviderFixture.SharedBooks);

        Assert.True(await FileAsync(DeviceOne, book));
        Assert.True(await FileAsync(DeviceOne, book));
        Assert.False(await FileAsync(DeviceTwo, book));
        Assert.Equal(1, Slots);
    }

    // What a consumer sends and what a provider takes are the same value.
    [Fact]
    public async Task The_hashes_a_consumer_makes_group_its_reads_by_device()
    {
        var hasher = new DeviceHasher(new byte[32]);
        var (server, user) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.True(await FileAsync(hasher.Hash(server, user, "phone")));
        Assert.True(await FileAsync(hasher.Hash(server, user, "phone")));
        Assert.False(await FileAsync(hasher.Hash(server, user, "tablet")));
        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task The_tracks_of_a_device_stream_in_one_slot()
    {
        _fixture.Own("playing", item: ProviderFixture.SharedTrack);
        _fixture.Own("next", item: ProviderFixture.SharedTrack);

        Assert.True(await FileAsync(DeviceOne, query: "?static=true&PlaySessionId=playing"));
        Assert.True(await FileAsync(DeviceOne, query: "?static=true&PlaySessionId=next"));

        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task A_track_is_grouped_on_the_hls_routes_too()
    {
        _fixture.Own("playing", item: ProviderFixture.SharedTrack);
        _fixture.Own("next", item: ProviderFixture.SharedTrack);
        var segment = ProviderFixture.Stock("DynamicHls", "GetHlsAudioSegment");

        var (_, first) = await RunAsync(segment, "GET", ProviderFixture.SharedTrack, "?PlaySessionId=playing", device: DeviceOne);
        var (_, second) = await RunAsync(segment, "GET", ProviderFixture.SharedTrack, "?PlaySessionId=next", device: DeviceOne);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, Slots);
    }

    [Fact]
    public async Task Anything_but_audio_ignores_the_device()
    {
        _fixture.Own("one");
        _fixture.Own("two");

        Assert.True(await FileAsync(DeviceOne, ProviderFixture.SharedItem, "?PlaySessionId=one"));
        Assert.False(await FileAsync(DeviceOne, ProviderFixture.SharedItem, "?PlaySessionId=two"));
        Assert.False(await FileAsync(DeviceOne, ProviderFixture.SharedItem));
        Assert.False(await FileAsync(DeviceOne));
    }

    [Fact]
    public async Task A_film_read_under_the_play_session_of_a_track_counts_on_its_own()
    {
        _fixture.Own("playing", item: ProviderFixture.SharedTrack);
        _fixture.Own("next", item: ProviderFixture.SharedTrack);
        Assert.True(await FileAsync(DeviceOne, query: "?PlaySessionId=playing"));
        Assert.True(await FileAsync(DeviceOne, query: "?PlaySessionId=next"));

        Assert.False(await FileAsync(DeviceOne, ProviderFixture.SharedItem, "?PlaySessionId=next"));
        Assert.Equal(1, Slots);
    }

    // The answer of PlaybackInfo through both filters, as on a server.
    private async Task<bool> PlaybackInfoAsync(string playSession, StringValues device)
    {
        var action = ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo");
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Headers[ProviderProtocol.DeviceHeader] = device;
        var route = new RouteData();
        route.Values["itemId"] = Route(ProviderFixture.SharedTrack);
        var actionContext = new ActionContext(http, route, action);
        var resource = new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
        var arguments = new Dictionary<string, object?> { ["itemId"] = ProviderFixture.SharedTrack, ["playbackInfoDto"] = null };
        var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, new object());
        var answer = new OkObjectResult(new PlaybackInfoResponse { PlaySessionId = playSession });
        var ran = false;
        await _filter.OnResourceExecutionAsync(resource, async () =>
        {
            await new PeerPlaybackInfoFilter(_fixture.Directory, _fixture.Catalog, _fixture.Slots, _fixture.Closer).OnActionExecutionAsync(executing, () =>
            {
                ran = true;
                return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()) { Result = answer });
            });
            return new ResourceExecutedContext(resource, new List<IFilterMetadata>());
        });
        return ran;
    }

    [Fact]
    public async Task The_tracks_a_device_opens_with_playback_info_stream_in_one_slot()
    {
        Assert.True(await PlaybackInfoAsync("playing", DeviceOne));
        Assert.True(await PlaybackInfoAsync("next", DeviceOne));
        Assert.True(await FileAsync(DeviceOne, query: "?static=true&PlaySessionId=playing"));

        // Taken again from nothing: only the item that PlaybackInfo recorded keeps them together.
        _fixture.Slots.ReleaseAll(_fixture.Peer.Id);
        Assert.True(await FileAsync(DeviceOne, query: "?static=true&PlaySessionId=playing"));
        Assert.True(await FileAsync(DeviceOne, query: "?static=true&PlaySessionId=next"));

        Assert.Equal(1, Slots);
        Assert.False(await PlaybackInfoAsync("other", DeviceTwo));
    }

    // Only a play session that keeps to its item shares the slot of a device.
    [Theory]
    [InlineData("Videos", "GetVideoStream", "?static=true&PlaySessionId=")]
    [InlineData("DynamicHls", "GetHlsAudioSegment", "?PlaySessionId=")]
    public async Task The_play_session_of_a_film_on_the_route_of_a_track_counts_on_its_own(string controller, string action, string query)
    {
        _fixture.Own("film", item: ProviderFixture.SharedItem);
        _fixture.Own("other film", item: ProviderFixture.SharedItem);
        var route = ProviderFixture.Stock(controller, action);

        var (_, first) = await RunAsync(route, "GET", ProviderFixture.SharedTrack, query + "film", device: DeviceOne);
        var (_, second) = await RunAsync(route, "GET", ProviderFixture.SharedTrack, query + "other%20film", device: DeviceOne);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, Slots);
    }

    [Theory]
    [InlineData("track")]
    [InlineData("TRACK")]
    public async Task The_play_session_of_a_track_that_read_a_film_counts_on_its_own_from_then_on(string named)
    {
        _fixture.Own("track", item: ProviderFixture.SharedTrack);
        Assert.True(await FileAsync(default, ProviderFixture.SharedItem, "?PlaySessionId=" + named));
        _fixture.Slots.Release(_fixture.Peer.Id, named);

        Assert.True(await FileAsync(DeviceOne, query: "?PlaySessionId=track"));
        Assert.False(await FileAsync(DeviceOne));
        Assert.Equal(1, Slots);
    }

    [Theory]
    [InlineData("")]
    [InlineData("phone")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task A_device_that_is_not_a_hash_counts_as_none(string device)
    {
        Assert.True(await FileAsync(device));

        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_repeated_device_counts_as_none()
    {
        Assert.True(await FileAsync(new StringValues([DeviceOne, DeviceTwo])));

        Assert.Equal(0, Slots);
    }

    [Fact]
    public async Task A_device_opens_nothing_that_is_not_shared()
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", ProviderFixture.PrivateTrack, "?static=true", device: DeviceOne);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Equal(0, Slots);
    }

    [Theory]
    [InlineData(true, DeviceOne)]
    [InlineData(false, null)]
    public async Task A_playback_info_notes_the_device_of_a_track_only(bool track, string? expected)
    {
        string? noted = "nothing ran";

        await RunAsync(
            ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"),
            "POST",
            track ? ProviderFixture.SharedTrack : ProviderFixture.SharedItem,
            ends: http => noted = PeerGuardFilter.NotedDevice(http),
            device: DeviceOne);

        Assert.Equal(expected, noted);
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
    [InlineData("MediaSegments", "GetItemSegments", null)]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", null)]
    [InlineData("Videos", "GetVideoStream", null)]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", "?PlaySessionId=play")]
    [InlineData("Audio", "GetAudioStream", null)]
    [InlineData("Lyrics", "GetLyrics", null)]
    [InlineData("Library", "GetFile", null)]
    [InlineData("Library", "GetDownload", null)]
    public async Task A_stub_does_not_exist_for_a_peer(string controller, string action, string? query)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.StubItem, query ?? string.Empty);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Theory]
    [InlineData("Image", "GetItemImage", null)]
    [InlineData("Subtitle", "GetSubtitle", null)]
    [InlineData("MediaSegments", "GetItemSegments", null)]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", null)]
    [InlineData("Videos", "GetVideoStream", null)]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", "?PlaySessionId=play")]
    [InlineData("Audio", "GetAudioStream", null)]
    [InlineData("Lyrics", "GetLyrics", null)]
    [InlineData("Library", "GetFile", null)]
    [InlineData("Library", "GetDownload", null)]
    public async Task An_item_outside_the_share_does_not_exist_for_a_peer(string controller, string action, string? query)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.PrivateItem, query ?? string.Empty);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    // Row 561: the File and Download of an admin's .strm in a shared library would be its text, a URL with any credentials.
    [Theory]
    [InlineData("GetFile", "/media/film/remote.strm")]
    [InlineData("GetDownload", "/media/film/remote.STRM")]
    public async Task The_strm_of_the_admin_is_never_given_to_a_peer(string action, string path)
    {
        var item = Guid.Parse("31000000-0000-0000-0000-000000000000");
        _fixture.AddItem(item, new MediaBrowser.Controller.Entities.Movies.Movie { Path = path }, ProviderFixture.SharedLibrary);

        var (context, ran) = await RunAsync(ProviderFixture.Stock("Library", action), "GET", item);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    // Row 565: a static stream of an admin's .strm would give its URL as the File route does.
    [Theory]
    [InlineData("Videos", "GetVideoStream", "?static=true")]
    [InlineData("Videos", "GetVideoStreamByContainer", "?Static=True")]
    [InlineData("Videos", "GetVideoStream", "?static=false&static=true")]
    [InlineData("Videos", "GetVideoStream", "?static=1")]
    [InlineData("Audio", "GetAudioStream", "?static=true")]
    [InlineData("Audio", "GetAudioStreamByContainer", "?STATIC=true")]
    [InlineData("Videos", "GetVideoStream", "?params=%3B%3Btrue")]
    public async Task A_static_stream_of_the_strm_of_the_admin_is_never_given_to_a_peer(string controller, string action, string query)
    {
        var item = Guid.Parse("31000000-0000-0000-0000-000000000000");
        _fixture.AddItem(item, new MediaBrowser.Controller.Entities.Movies.Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", item, query);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Equal(0, Slots);
    }

    [Theory]
    [InlineData("?mediaSourceId=31000000000000000000000000000000&static=true")]
    [InlineData("?MediaSourceId=31000000-0000-0000-0000-000000000000&static=true")]
    public async Task A_static_stream_that_names_the_strm_of_the_admin_as_a_version_is_never_given_to_a_peer(string query)
    {
        var version = Guid.Parse("31000000-0000-0000-0000-000000000000");
        _fixture.AddItem(version, new MediaBrowser.Controller.Entities.Movies.Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);

        var (context, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", ProviderFixture.SharedItem, query);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    // Row 568: an item of the query may be what stock acts on in place of the route's.
    [Theory]
    [InlineData("?itemId=40000000000000000000000000000000")]
    [InlineData("?itemId=60000000-0000-0000-0000-000000000000")]
    [InlineData("?itemId=30000000000000000000000000000000&itemId=40000000000000000000000000000000")]
    [InlineData("?ItemId=40000000-0000-0000-0000-000000000000")]
    [InlineData("?ITEMID=60000000000000000000000000000000")]
    [InlineData("?itemId=not-a-guid")]
    [InlineData("?itemId=%20")]
    [InlineData("?itemId=00000000000000000000000000000000")]
    public async Task An_item_of_the_query_that_is_not_the_route_item_does_not_exist_for_a_peer(string query)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock("Subtitle", "GetSubtitleWithTicks"), "GET", ProviderFixture.SharedItem, query);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Theory]
    [InlineData("Image", "GetItemImage", "")]
    [InlineData("MediaSegments", "GetItemSegments", "")]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", "")]
    [InlineData("Videos", "GetVideoStream", "")]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", "&PlaySessionId=play")]
    [InlineData("Audio", "GetAudioStream", "")]
    [InlineData("Lyrics", "GetLyrics", "")]
    [InlineData("Library", "GetFile", "")]
    public async Task On_every_route_an_item_of_the_query_outside_the_share_does_not_exist_for_a_peer(string controller, string action, string rest)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem, "?itemId=" + Route(ProviderFixture.PrivateItem) + rest);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Equal(0, Slots);
    }

    // A query sent as one encoded key reaches stock as keys that differ by case, and each one is read.
    [Fact]
    public async Task An_item_of_the_query_under_a_key_of_another_case_is_read_too()
    {
        var decoded = Decoded(("itemId", Route(ProviderFixture.SharedItem)), ("ITEMID", Route(ProviderFixture.PrivateItem)));

        var (context, ran) = await RunAsync(ProviderFixture.Stock("Subtitle", "GetSubtitleWithTicks"), "GET", ProviderFixture.SharedItem, decoded: decoded);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Theory]
    [InlineData("Subtitle", "GetSubtitleWithTicks", "?itemId=30000000000000000000000000000000")]
    [InlineData("Subtitle", "GetSubtitleWithTicks", "?ItemId=30000000-0000-0000-0000-000000000000&itemId=30000000000000000000000000000000")]
    [InlineData("Subtitle", "GetSubtitleWithTicks", "?itemId=")]
    [InlineData("MediaInfo", "GetPostedPlaybackInfo", "?itemId=30000000-0000-0000-0000-000000000000")]
    public async Task An_item_of_the_query_that_is_the_route_item_goes_on(string controller, string action, string query)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem, query);

        Assert.True(ran);
        Assert.Null(context.Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?static=false")]
    public async Task A_stream_of_the_strm_of_the_admin_that_is_not_static_goes_on(string query)
    {
        var item = Guid.Parse("31000000-0000-0000-0000-000000000000");
        _fixture.AddItem(item, new MediaBrowser.Controller.Entities.Movies.Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);

        var (_, ran) = await RunAsync(ProviderFixture.Stock("Videos", "GetVideoStream"), "GET", item, query);

        Assert.True(ran);
    }

    [Theory]
    [InlineData("Videos", "GetVideoStream")]
    [InlineData("Audio", "GetAudioStream")]
    public async Task A_static_stream_of_a_shared_file_is_given_to_a_peer(string controller, string action)
    {
        var (_, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem, "?static=true");

        Assert.True(ran);
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task The_file_of_a_shared_item_is_given_to_a_peer(string action)
    {
        var (_, ran) = await RunAsync(ProviderFixture.Stock("Library", action), "GET", ProviderFixture.SharedItem);

        Assert.True(ran);
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
    public async Task The_media_segments_of_a_shared_item_are_served()
    {
        var (_, ran) = await RunAsync(ProviderFixture.Stock("MediaSegments", "GetItemSegments"), "GET", ProviderFixture.SharedItem);

        Assert.True(ran);
    }

    [Fact]
    public async Task The_lyrics_of_a_shared_track_are_served_with_no_slot()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "other");

        var (_, ran) = await RunAsync(ProviderFixture.Stock("Lyrics", "GetLyrics"), "GET", ProviderFixture.SharedItem);

        Assert.True(ran);
    }

    [Theory]
    [InlineData("Library", "GetFile")]
    [InlineData("Library", "GetDownload")]
    public async Task A_folder_has_no_file_for_a_peer(string controller, string action)
    {
        var folder = Guid.NewGuid();
        _fixture.AddItem(folder, new MediaBrowser.Controller.Entities.Folder { Path = "/media/film/Extra" }, ProviderFixture.SharedLibrary);

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", folder);

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
        Assert.Equal(0, Slots);
    }

    [Theory]
    [InlineData("Videos", "GetVideoStream", "GET")]
    [InlineData("Audio", "GetAudioStream", "GET")]
    [InlineData("Audio", "GetAudioStreamByContainer", "HEAD")]
    [InlineData("Library", "GetFile", "GET")]
    [InlineData("Library", "GetFile", "HEAD")]
    [InlineData("Library", "GetDownload", "GET")]
    public async Task A_stream_holds_its_slot_while_it_is_written(string controller, string action, string method)
    {
        var during = -1;

        await RunAsync(ProviderFixture.Stock(controller, action), method, ProviderFixture.SharedItem, string.Empty, () => during = _fixture.Slots.Count(_fixture.Peer.Id));

        Assert.Equal(1, during);
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Theory]
    [InlineData("Videos", "GetVideoStream", "?PlaySessionId=play")]
    [InlineData("Audio", "GetAudioStream", "?PlaySessionId=play")]
    [InlineData("Library", "GetFile", "")]
    [InlineData("Library", "GetDownload", "")]
    public async Task A_stream_over_the_limit_is_refused_with_the_signal(string controller, string action, string query)
    {
        _fixture.Own("play");
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "other");

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action), "GET", ProviderFixture.SharedItem, query);

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
    [InlineData("Audio", "GetAudioStream")]
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
    [InlineData("Audio", "GetAudioStreamByContainer")]
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
    private const string DeviceHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly ProviderFixture _fixture = new();
    private readonly PeerPlaybackInfoFilter _filter;

    public PeerPlaybackInfoFilterTests()
    {
        _filter = new PeerPlaybackInfoFilter(_fixture.Directory, _fixture.Catalog, _fixture.Slots, _fixture.Closer);
    }

    private Task<(ActionExecutingContext Context, bool Ran)> RunAsync(IActionResult? result, string? replaces = null, Exception? failure = null, bool consumerLeft = false, Dictionary<string, object?>? arguments = null, bool bodyBound = true, string? device = null)
    {
        // Stock binds the posted body of every consumer PlaybackInfo, so the tests post one unless told otherwise.
        arguments ??= new Dictionary<string, object?>();
        arguments.TryAdd("itemId", ProviderFixture.SharedItem);
        if (bodyBound)
        {
            arguments.TryAdd("playbackInfoDto", new PostedBody());
        }

        return RunAsync(ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"), result, replaces, failure, consumerLeft, arguments, device);
    }

    private async Task<(ActionExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, IActionResult? result, string? replaces = null, Exception? failure = null, bool consumerLeft = false, Dictionary<string, object?>? arguments = null, string? device = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        PeerGuardFilter.NoteDevice(http, device);
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

    // This filter lets everyone by while the peers cannot be read; StoreNotLoadedFilter stops non-admins (D32).
    [Fact]
    public async Task While_the_peers_cannot_be_read_a_local_user_plays_as_on_stock()
    {
        _fixture.Caller = new Jellyfin.Database.Implementations.Entities.User("mario", "auth", "reset");
        _fixture.Store.Unavailable = true;

        var (context, ran) = await RunAsync(Answer());

        Assert.True(ran);
        Assert.Null(context.Result);
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
    public async Task A_playback_that_comes_while_the_timer_closes_the_abandoned_transcode_waits_for_it()
    {
        var peer = _fixture.Peer.Id;
        _fixture.Slots.Confirm(peer, _fixture.Slots.TryReserve(peer, 1, null)!, "gone");
        var request = _fixture.Slots.TryEnter(peer, 1, "gone")!;
        request.Abandon();
        request.Dispose();
        _fixture.Clock.Advance(StreamSlots.AbandonGrace);
        var killing = new TaskCompletionSource();
        _fixture.Transcodes.Setup(manager => manager.KillTranscodingJobs(It.IsAny<string>(), "gone", It.IsAny<Func<string, bool>>())).Returns(killing.Task);
        var timer = _fixture.Closer.CloseAbandonedAsync();

        var (context, ran) = await RunAsync(Answer("next"));

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (context.Result as StatusCodeResult)?.StatusCode);

        killing.SetResult();
        await timer;
        var (_, ranAfter) = await RunAsync(Answer("next"));

        Assert.True(ranAfter);
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
    public async Task The_next_track_of_a_device_passes_on_a_full_server()
    {
        await RunAsync(Answer("playing"), device: "phone");
        var (next, ran) = await RunAsync(Answer("next"), device: "phone");
        var (other, otherRan) = await RunAsync(Answer("other"), device: "tablet");

        Assert.True(ran);
        Assert.Null(next.Result);
        Assert.False(otherRan);
        Assert.Equal(StatusCodes.Status429TooManyRequests, (other.Result as StatusCodeResult)?.StatusCode);
        Assert.Equal(1, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.True(_fixture.Slots.Owns(_fixture.Peer.Id, "next"));
    }

    // The guard notes the device of audio alone, so the header by itself groups nothing.
    [Fact]
    public async Task A_playback_reads_no_device_from_the_header()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "playing", device: DeviceHash);
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Headers[ProviderProtocol.DeviceHeader] = DeviceHash;
        var actionContext = new ActionContext(http, new RouteData(), ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"));
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?> { ["playbackInfoDto"] = new PostedBody() }, new object());

        await _filter.OnActionExecutionAsync(context, () => throw new InvalidOperationException("It must not run"));

        Assert.Equal(StatusCodes.Status429TooManyRequests, (context.Result as StatusCodeResult)?.StatusCode);
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
        answer.MediaSources = [new MediaBrowser.Model.Dto.MediaSourceInfo { Id = ProviderFixture.SharedItem.ToString("N"), TranscodingUrl = transcodingUrl }];
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

        public string? MediaSourceId { get; init; }
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
