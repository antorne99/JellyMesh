using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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
    private static readonly Guid _localId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly CoordinatorFixture _fixture = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<ITranscodeManager> _transcodes = new();
    private readonly Mock<IFederationResolver> _resolver = new();
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly StubEntry _stub;

    public PlaybackFiltersTests()
    {
        _stub = PlaybackValues.Stub(PlaybackValues.Source());
        var video = new Movie { ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature") };
        _library.Setup(library => library.GetItemById(_itemId)).Returns(video);
        _stubs.Setup(stubs => stubs.Find("stub")).Returns(_stub);
        _resolver.Setup(resolver => resolver.Resolve()).Returns(new FederationView(IndexHealth.Ok, 0, [], []));
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
            new PlaybackRefusal(_sessionManager.Object, NullLogger<PlaybackRefusal>.Instance),
            new LocalCopyLookup(_resolver.Object, _library.Object));
    }

    private StockRouteGuardFilter GuardFilter()
    {
        return new StockRouteGuardFilter(
            new StubLookup(_library.Object, _stubs.Object),
            _library.Object,
            _authorization.Object,
            _fixture.Coordinator,
            _fixture.Sessions,
            new PlaybackRefusal(_sessionManager.Object, NullLogger<PlaybackRefusal>.Instance),
            _transcodes.Object);
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

    private void LocalCopyArrived(bool visible = true, bool isLocal = true)
    {
        var identity = new ContentIdentity("key", "work", ContentKind.Movie, MatchKind.ProviderId);
        var content = new ResolvedContent(identity, isLocal, null, [])
        {
            LocalItemIds = [_localId],
            RemoteItems = [new ResolvedItem(_stub.Sources[0].ServerId, _stub.Sources[0].ItemId)]
        };
        _resolver.Setup(resolver => resolver.Resolve()).Returns(new FederationView(IndexHealth.Ok, 1, [content], []));
        if (visible)
        {
            _library.Setup(library => library.GetItemById<BaseItem>(_localId, _user)).Returns(new Movie { Id = _localId });
        }
    }

    private void Offline()
    {
        CanSeeItem();
        _fixture.Registry.Offline.Add(_stub.Sources[0].ServerId);
    }

    private static MediaSourceInfo LocalFile(bool directPlay) => new()
    {
        Id = _localId.ToString("N"),
        Path = "/media/film/secret-local-path.mp4",
        Protocol = MediaProtocol.File,
        Container = "mov,mp4,m4a,3gp,3g2,mj2",
        SupportsDirectPlay = directPlay,
        SupportsDirectStream = directPlay,
        SupportsTranscoding = true,
        TranscodingUrl = directPlay ? null : "/videos/" + _localId.ToString("N") + "/master.m3u8?ApiKey=" + Token,
        MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Path = "/media/film/secret-local-path.mp4" }]
    };

    private async Task<ActionExecutedContext> RunStockAsync(ActionExecutingContext context, MediaSourceInfo source)
    {
        var executed = new ActionExecutedContext(context, [], new object())
        {
            Result = new OkObjectResult(new PlaybackInfoResponse { MediaSources = [source], PlaySessionId = "local-session" })
        };
        await InfoFilter().OnActionExecutionAsync(context, () => Task.FromResult(executed));
        return executed;
    }

    [Fact]
    public async Task A_local_copy_answers_for_a_stub_whose_server_is_offline()
    {
        Offline();
        LocalCopyArrived();
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId), ("mediaSourceId", _itemId.ToString("N")));

        var executed = await RunStockAsync(context, LocalFile(directPlay: true));

        Assert.Null(context.Result);
        Assert.Equal(_localId, context.ActionArguments["itemId"]);
        Assert.Equal(_localId.ToString("N"), context.ActionArguments["mediaSourceId"]);
        var response = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(executed.Result).Value);
        var source = Assert.Single(response.MediaSources);
        Assert.StartsWith("/Videos/" + _localId.ToString("N") + "/stream.mp4?static=true", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Contains("ApiKey=" + Token, source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Equal(_itemId.ToString("N"), source.Id);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Null(source.Path);
        Assert.Null(source.MediaStreams[0].Path);
    }

    [Fact]
    public async Task A_local_copy_the_client_cannot_play_keeps_the_stock_transcode()
    {
        Offline();
        LocalCopyArrived();
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        var executed = await RunStockAsync(context, LocalFile(directPlay: false));

        var response = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(executed.Result).Value);
        Assert.StartsWith("/videos/" + _localId.ToString("N") + "/master.m3u8", response.MediaSources[0].TranscodingUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_copy_in_a_library_the_user_cannot_see_does_not_count()
    {
        Offline();
        LocalCopyArrived(visible: false);
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_copy_of_another_title_does_not_count()
    {
        Offline();
        var identity = new ContentIdentity("other", "other", ContentKind.Movie, MatchKind.ProviderId);
        var content = new ResolvedContent(identity, true, null, []) { LocalItemIds = [_localId] };
        _resolver.Setup(resolver => resolver.Resolve()).Returns(new FederationView(IndexHealth.Ok, 1, [content], []));
        _library.Setup(library => library.GetItemById<BaseItem>(_localId, _user)).Returns(new Movie { Id = _localId });
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stub_with_content_still_to_serve_is_not_replaced_by_the_part_that_arrived()
    {
        Offline();
        var remote = new ResolvedItem(_stub.Sources[0].ServerId, _stub.Sources[0].ItemId);
        var arrived = new ResolvedContent(new ContentIdentity("e1", "show", ContentKind.Episode, MatchKind.ProviderId, 1, 1), true, null, [])
        {
            LocalItemIds = [_localId],
            RemoteItems = [remote]
        };
        var missing = new ResolvedContent(new ContentIdentity("e2", "show", ContentKind.Episode, MatchKind.ProviderId, 1, 2), false, null, []) { RemoteItems = [remote] };
        _resolver.Setup(resolver => resolver.Resolve()).Returns(new FederationView(IndexHealth.Ok, 1, [arrived, missing], []));
        _library.Setup(library => library.GetItemById<BaseItem>(_localId, _user)).Returns(new Movie { Id = _localId });
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stock_failure_on_the_local_copy_is_a_503_and_shows_no_stock_body()
    {
        Offline();
        LocalCopyArrived();
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));
        var failed = new ActionExecutedContext(context, [], new object()) { Result = new NotFoundResult() };

        await InfoFilter().OnActionExecutionAsync(context, () => Task.FromResult(failed));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(failed.Result).StatusCode);
    }

    [Fact]
    public async Task A_stock_answer_with_an_error_code_on_the_local_copy_is_a_503()
    {
        Offline();
        LocalCopyArrived();
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));
        var failed = new ActionExecutedContext(context, [], new object())
        {
            Result = new OkObjectResult(new PlaybackInfoResponse { MediaSources = [], ErrorCode = PlaybackErrorCode.NoCompatibleStream })
        };

        await InfoFilter().OnActionExecutionAsync(context, () => Task.FromResult(failed));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(failed.Result).StatusCode);
    }

    [Fact]
    public async Task A_working_remote_never_needs_the_whole_library_read()
    {
        CanSeeItem();
        LocalCopyArrived();

        await RunAsync(InfoFilter(), Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId)));

        _resolver.Verify(resolver => resolver.Resolve(), Times.Never);
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

    [Theory]
    [InlineData("HlsSegment", "StopEncodingProcess")]
    [InlineData("Playstate", "OnPlaybackStopped")]
    [InlineData("Playstate", "OnPlaybackStoppedLegacy")]
    public async Task Another_user_cannot_stop_a_stub_played_on_this_server(string controller, string action)
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play");
        var context = Context(controller, action, ("playSessionId", "local-play"));

        var passed = await RunAsync(GuardFilter(), context);

        Assert.False(passed);
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Fact]
    public async Task Another_user_cannot_report_a_stub_played_on_this_server_as_stopped()
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play");
        var context = Context("Playstate", "ReportPlaybackStopped", ("playbackStopInfo", new MediaBrowser.Model.Session.PlaybackStopInfo { PlaySessionId = "LOCAL-PLAY" }));

        var passed = await RunAsync(GuardFilter(), context);

        Assert.False(passed);
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Theory]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    [InlineData("DynamicHls", "GetVariantHlsVideoPlaylist")]
    [InlineData("Videos", "GetVideoStream")]
    public async Task A_stream_cannot_name_the_local_play_session_of_another_user(string controller, string action)
    {
        CanSeeItem();
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play");
        var context = Context(controller, action, ("itemId", _itemId), ("playSessionId", "LOCAL-PLAY"));

        var passed = await RunAsync(GuardFilter(), context);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, (context.Result as StatusCodeResult)?.StatusCode);
    }

    [Fact]
    public async Task A_stream_of_its_own_local_play_session_passes()
    {
        CanSeeItem();
        _fixture.Sessions.RecordLocal(_user.Id, "local-play");

        Assert.True(await RunAsync(GuardFilter(), Context("DynamicHls", "GetHlsVideoSegment", ("itemId", _itemId), ("playSessionId", "local-play"))));
    }

    [Fact]
    public async Task Its_user_can_stop_a_stub_played_on_this_server()
    {
        _fixture.Sessions.RecordLocal(_user.Id, "local-play");

        Assert.True(await RunAsync(GuardFilter(), Context("HlsSegment", "StopEncodingProcess", ("playSessionId", "local-play"))));
        Assert.True(await RunAsync(GuardFilter(), Context("Playstate", "ReportPlaybackStopped", ("playbackStopInfo", new MediaBrowser.Model.Session.PlaybackStopInfo { PlaySessionId = "local-play" }))));
    }

    [Fact]
    public async Task A_play_session_nobody_recorded_stays_with_stock()
    {
        Assert.True(await RunAsync(GuardFilter(), Context("HlsSegment", "StopEncodingProcess", ("playSessionId", "real-title"))));
        Assert.True(await RunAsync(GuardFilter(), Context("Playstate", "OnPlaybackStopped", ("playSessionId", "real-title"))));
    }

    private ActionExecutingContext SegmentOfStub(string? playSessionId = null)
    {
        CanSeeItem();
        return Context("DynamicHls", "GetHlsVideoSegment", ("itemId", _itemId), ("playSessionId", playSessionId));
    }

    private async Task FileAtLimitAsync()
    {
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.StreamLimitReached));
        await _fixture.Coordinator.OpenFileAsync(_stub, HttpMethod.Get, null, CancellationToken.None);
    }

    private async Task<ActionExecutedContext> FailStockAsync(ActionExecutingContext context, Exception failure)
    {
        ActionExecutedContext? executed = null;
        await GuardFilter().OnActionExecutionAsync(context, () =>
        {
            executed = new ActionExecutedContext(context, [], new object()) { Exception = failure };
            return Task.FromResult(executed);
        });
        return executed!;
    }

    [Fact]
    public async Task A_local_transcode_that_lost_the_slot_gets_the_refusal_of_the_limit()
    {
        await FileAtLimitAsync();

        var executed = await FailStockAsync(SegmentOfStub(), new InvalidOperationException());

        Assert.Null(executed.Exception);
        Assert.True(executed.ExceptionHandled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, (executed.Result as StatusCodeResult)?.StatusCode);
    }

    [Fact]
    public async Task The_refusal_clears_the_failed_job_a_retry_would_wait_for()
    {
        await FileAtLimitAsync();

        await FailStockAsync(SegmentOfStub("local-play"), new FfmpegException("exited"));

        _transcodes.Verify(manager => manager.KillTranscodingJobs("device", "local-play", It.IsAny<Func<string, bool>>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_start_of_ffmpeg_for_any_reason_is_cleared_and_stays_a_failure()
    {
        var failure = new FfmpegException("exited");

        var executed = await FailStockAsync(SegmentOfStub("local-play"), failure);

        Assert.Same(failure, executed.Exception);
        _transcodes.Verify(manager => manager.KillTranscodingJobs("device", "local-play", It.IsAny<Func<string, bool>>()), Times.Once);
    }

    [Fact]
    public async Task A_refusal_without_a_play_session_stops_nothing_of_the_device()
    {
        await FileAtLimitAsync();

        await FailStockAsync(SegmentOfStub(), new FfmpegException("exited"));

        _transcodes.Verify(manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()), Times.Never);
    }

    [Fact]
    public async Task A_local_transcode_that_fails_for_any_other_reason_stays_a_failure()
    {
        var failure = new InvalidOperationException();

        var executed = await FailStockAsync(SegmentOfStub("local-play"), failure);

        Assert.Same(failure, executed.Exception);
        Assert.False(executed.ExceptionHandled);
        _transcodes.Verify(manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()), Times.Never);
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
