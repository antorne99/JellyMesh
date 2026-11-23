using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Segments;
using Jellyfin.Plugin.JellyMesh.Tests.Segments;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Lyrics;
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
    private readonly Mock<IMediaSegmentManager> _segmentManager = new();
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly StreamSlots _peerSlots = new(TimeProvider.System, new InMemoryConfigurationStore());
    private readonly StubEntry _stub;
    private SegmentOffer? _offer;

    public PlaybackFiltersTests()
    {
        _stub = PlaybackValues.Stub(PlaybackValues.Source());
        var video = new Movie { ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature") };
        _library.Setup(library => library.GetItemById(_itemId)).Returns(video);
        _stubs.Setup(stubs => stubs.Find("stub")).Returns(_stub);
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([new Folder()]);
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

    private PlaybackInfoFilter InfoFilter(InMemoryConfigurationStore? store = null)
    {
        store ??= new InMemoryConfigurationStore();
        var lookup = new StubLookup(_library.Object, _stubs.Object, store);
        _offer = new SegmentOffer(_segmentManager.Object, Segments(lookup, store), lookup, _fixture.Sessions);
        return new PlaybackInfoFilter(
            lookup,
            _authorization.Object,
            _fixture.Coordinator,
            new PlaybackRefusal(_sessionManager.Object, NullLogger<PlaybackRefusal>.Instance),
            new LocalCopyLookup(_resolver.Object, _library.Object, _stubs.Object),
            new PlaybackVersions(lookup, _stubs.Object, _library.Object, new Mock<IFederationIndex>().Object, store),
            _offer);
    }

    private StubSegments Segments(StubLookup lookup, InMemoryConfigurationStore store)
    {
        var index = new Mock<IFederationIndex>();
        index.Setup(federation => federation.Snapshot).Returns(new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = [] });
        return new(lookup, index.Object, _fixture.Registry, new StubSegmentsTests.FakeSegmentClient(), store, TimeProvider.System, NullLogger<StubSegments>.Instance);
    }

    private StockRouteGuardFilter GuardFilter(PlaybackSessionStore? sessions = null, Func<string, IReadOnlyList<string>>? listFolder = null, InMemoryConfigurationStore? store = null)
    {
        return new StockRouteGuardFilter(
            new StubLookup(_library.Object, _stubs.Object, store ?? new InMemoryConfigurationStore()),
            _authorization.Object,
            _fixture.Coordinator,
            sessions ?? _fixture.Sessions,
            new PlaybackRefusal(_sessionManager.Object, NullLogger<PlaybackRefusal>.Instance),
            _transcodes.Object,
            _peerSlots,
            new AudioStubRoutes(_fixture.Coordinator, _fixture.Sessions, new PlaybackRefusal(_sessionManager.Object, NullLogger<PlaybackRefusal>.Instance), _fixture.Clock),
            listFolder ?? StockRouteGuardFilter.ListFolder);
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
    [InlineData("VideoAttachments", "GetAttachment", "videoId")]
    [InlineData("Trickplay", "GetTrickplayHlsPlaylist", "itemId")]
    [InlineData("MediaSegments", "GetItemSegments", "itemId")]
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
    public async Task The_segments_of_a_stub_are_not_found_for_a_user_who_cannot_see_it()
    {
        var context = Context("MediaSegments", "GetItemSegments", ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task The_segments_of_a_stub_in_no_library_are_not_found()
    {
        CanSeeItem();
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        var context = Context("MediaSegments", "GetItemSegments", ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task The_segments_of_a_stub_reach_a_user_who_can_see_it()
    {
        CanSeeItem();

        Assert.True(await RunAsync(GuardFilter(), Context("MediaSegments", "GetItemSegments", ("itemId", _itemId))));
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

    private Guid LocalItem()
    {
        var local = Guid.NewGuid();
        _library.Setup(library => library.GetItemById(local)).Returns(new Movie());
        _library.Setup(library => library.GetItemById<BaseItem>(local, _user)).Returns(new Movie());
        return local;
    }

    // The stock actions take a version as text, Trickplay as a Guid.
    [Theory]
    [InlineData("Videos", "GetVideoStream", "itemId", "mediaSourceId", true)]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", "itemId", "mediaSourceId", true)]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId", "routeMediaSourceId", true)]
    [InlineData("VideoAttachments", "GetAttachment", "videoId", "mediaSourceId", true)]
    [InlineData("Trickplay", "GetTrickplayHlsPlaylist", "itemId", "mediaSourceId", false)]
    public async Task A_stock_media_route_refuses_a_stub_named_as_the_version_without_a_user(string controller, string action, string item, string version, bool asText)
    {
        var local = LocalItem();
        Anonymous();
        var context = Context(controller, action, (item, local), (version, asText ? _itemId.ToString("N") : _itemId));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stub_named_as_the_version_is_checked_and_not_the_item()
    {
        var context = Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("mediaSourceId", _itemId.ToString("N")));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stub_named_as_the_version_serves_a_user_who_can_see_it()
    {
        CanSeeItem();

        Assert.True(await RunAsync(GuardFilter(), Context("VideoAttachments", "GetAttachment", ("videoId", LocalItem()), ("mediaSourceId", _itemId.ToString("N")))));
    }

    [Fact]
    public async Task Every_stub_a_route_names_must_be_visible()
    {
        CanSeeItem();
        var hidden = Guid.NewGuid();
        _library.Setup(library => library.GetItemById(hidden)).Returns(new Movie { ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "hidden", "signature") });
        _stubs.Setup(stubs => stubs.Find("hidden")).Returns(PlaybackValues.Stub(PlaybackValues.Source()) with { Id = "hidden" });
        var context = Context("Subtitle", "GetSubtitle", ("routeItemId", _itemId), ("routeMediaSourceId", hidden.ToString("N")));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_version_that_names_no_stub_is_untouched(string? version)
    {
        Anonymous();

        Assert.True(await RunAsync(GuardFilter(), Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("mediaSourceId", version))));
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
        Assert.Equal(_localId.ToString("N"), source.Id);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Null(source.Path);
        Assert.Null(source.MediaStreams[0].Path);
    }

    [Fact]
    public async Task A_device_playing_the_local_copy_reads_no_segments_of_the_stub_directly()
    {
        Offline();
        LocalCopyArrived();
        _segmentManager.Setup(manager => manager.HasSegments(It.IsAny<Guid>())).Returns(true);
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));
        var file = LocalFile(directPlay: true);
        file.HasSegments = true;

        var executed = await RunStockAsync(context, file);

        var response = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(executed.Result).Value);
        Assert.True(Assert.Single(response.MediaSources).HasSegments);
        Assert.False(_offer!.ServesDirect(_user.Id, "device", _itemId));
        Assert.True(_offer.ServesDirect(_user.Id, "device", _localId));
        _fixture.Sessions.EndLocal(_user.Id, "local-session");
        Assert.True(_offer.ServesDirect(_user.Id, "device", _itemId));
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
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = Context(controller, action, ("playSessionId", "local-play"));

        var passed = await RunAsync(GuardFilter(), context);

        Assert.False(passed);
        Assert.IsType<NoContentResult>(context.Result);
        Assert.True(_fixture.Sessions.IsLocalOfAnother(_user.Id, "local-play"));
    }

    [Fact]
    public async Task Another_user_cannot_report_a_stub_played_on_this_server_as_stopped()
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
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
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = Context(controller, action, ("itemId", _itemId), ("playSessionId", "LOCAL-PLAY"));

        var passed = await RunAsync(GuardFilter(), context);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, (context.Result as StatusCodeResult)?.StatusCode);
    }

    [Fact]
    public async Task A_stream_of_its_own_local_play_session_passes()
    {
        CanSeeItem();
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), Context("DynamicHls", "GetHlsVideoSegment", ("itemId", _itemId), ("playSessionId", "local-play"))));
    }

    [Fact]
    public async Task Its_user_can_stop_a_stub_played_on_this_server()
    {
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), Context("HlsSegment", "StopEncodingProcess", ("playSessionId", "local-play"))));
        Assert.True(await RunAsync(GuardFilter(), Context("Playstate", "ReportPlaybackStopped", ("playbackStopInfo", new MediaBrowser.Model.Session.PlaybackStopInfo { PlaySessionId = "local-play" }))));
    }

    [Fact]
    public async Task A_play_session_nobody_recorded_stays_with_stock()
    {
        Assert.True(await RunAsync(GuardFilter(), Context("HlsSegment", "StopEncodingProcess", ("playSessionId", "real-title"))));
        Assert.True(await RunAsync(GuardFilter(), Context("Playstate", "OnPlaybackStopped", ("playSessionId", "real-title"))));
    }

    // Stock reads the legacy params as fields split on ';': the version is field 2, the play session field 20.
    private static string LegacyParams(int field, string value) => new string(';', field) + value;

    [Theory]
    [InlineData(null)]
    [InlineData("other-version")]
    public async Task A_stub_named_as_the_version_in_the_legacy_params_is_guarded(string? mediaSourceId)
    {
        Anonymous();
        var context = Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("mediaSourceId", mediaSourceId), ("params", LegacyParams(2, _itemId.ToString("N"))));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task Legacy_params_that_name_no_stub_are_untouched()
    {
        Anonymous();

        Assert.True(await RunAsync(GuardFilter(), Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("params", "profile;device;not-a-guid;true"))));
    }

    [Theory]
    [InlineData("playSessionId", "LOCAL-PLAY")]
    [InlineData("params", ";;;;;;;;;;;;;;;;;;;;local-play")]
    public async Task A_local_play_session_of_a_stub_names_the_stub_without_a_user(string argument, string value)
    {
        Anonymous();
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);
        var context = Context("Videos", "GetVideoStream", ("itemId", LocalItem()), (argument, value));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_local_play_session_of_a_stub_the_user_cannot_see_is_not_found()
    {
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);
        var context = Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("playSessionId", "local-play"));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_local_play_session_of_another_user_is_not_found_through_another_item()
    {
        CanSeeItem();
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("params", LegacyParams(20, "local-play")));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task Its_user_can_name_the_local_play_session_of_a_stub_through_another_item()
    {
        CanSeeItem();
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("playSessionId", "local-play"))));
    }

    private void StubGone()
    {
        _library.Setup(library => library.GetItemById(_itemId)).Returns((BaseItem?)null);
    }

    [Theory]
    [InlineData("playSessionId", "LOCAL-PLAY")]
    [InlineData("params", ";;;;;;;;;;;;;;;;;;;;local-play")]
    public async Task A_local_play_session_of_another_user_stays_guarded_when_its_stub_is_gone(string argument, string value)
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        StubGone();
        var context = Context("Videos", "GetVideoStream", ("itemId", LocalItem()), (argument, value));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_local_play_session_whose_stub_is_gone_still_wants_a_user()
    {
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);
        StubGone();
        Anonymous();
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("playSessionId", "local-play"));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task Its_user_can_name_the_local_play_session_of_a_stub_that_is_gone()
    {
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);
        StubGone();

        Assert.True(await RunAsync(GuardFilter(), Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("playSessionId", "local-play"))));
    }

    [Fact]
    public async Task A_local_play_session_its_owner_keeps_using_stays_guarded_however_many_they_open()
    {
        var owner = Guid.NewGuid();
        _fixture.Sessions.RecordLocal(owner, "local-play", _itemId);
        for (var index = 0; index <= PlaySessionOwners.MaxPerOwner; index++)
        {
            if (index % 25 == 0)
            {
                _fixture.Sessions.UseLocal(owner, "local-play");
            }

            _fixture.Sessions.RecordLocal(owner, "flood" + index, _itemId);
        }

        var context = Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("playSessionId", "local-play"));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("HlsSegment", "StopEncodingProcess")]
    [InlineData("Playstate", "OnPlaybackStopped")]
    [InlineData("Playstate", "OnPlaybackStoppedLegacy")]
    public async Task A_local_play_session_leaves_the_record_when_its_user_stops_it(string controller, string action)
    {
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), Context(controller, action, ("playSessionId", "LOCAL-PLAY"))));

        Assert.False(_fixture.Sessions.IsLocalOfAnother(Guid.NewGuid(), "local-play"));
    }

    [Fact]
    public async Task A_local_play_session_leaves_the_record_when_its_user_reports_it_stopped()
    {
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), Context("Playstate", "ReportPlaybackStopped", ("playbackStopInfo", new MediaBrowser.Model.Session.PlaybackStopInfo { PlaySessionId = "local-play" }))));

        Assert.False(_fixture.Sessions.IsLocalOfAnother(Guid.NewGuid(), "local-play"));
    }

    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private static readonly PlaybackTimes RetentionTimes = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), Retention, Retention);

    public static TheoryData<string, string, string, object> ReportsOfAPlaySession => new()
    {
        { "Playstate", "ReportPlaybackStart", "playbackStartInfo", new MediaBrowser.Model.Session.PlaybackStartInfo { PlaySessionId = "local-play" } },
        { "Playstate", "OnPlaybackStart", "playSessionId", "local-play" },
        { "Playstate", "OnPlaybackStartLegacy", "playSessionId", "local-play" },
        { "Playstate", "ReportPlaybackProgress", "playbackProgressInfo", new MediaBrowser.Model.Session.PlaybackProgressInfo { PlaySessionId = "LOCAL-PLAY" } },
        { "Playstate", "OnPlaybackProgress", "playSessionId", "local-play" },
        { "Playstate", "OnPlaybackProgressLegacy", "playSessionId", "local-play" },
        { "Playstate", "PingPlaybackSession", "playSessionId", "local-play" }
    };

    [Theory]
    [MemberData(nameof(ReportsOfAPlaySession))]
    public async Task A_report_of_another_user_on_a_local_play_session_is_answered_without_effect(string controller, string action, string argument, object value)
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = Context(controller, action, (argument, value));

        var passed = await RunAsync(GuardFilter(), context);

        Assert.False(passed);
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Theory]
    [MemberData(nameof(ReportsOfAPlaySession))]
    public async Task A_report_of_its_user_on_a_local_play_session_reaches_stock(string controller, string action, string argument, object value)
    {
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), Context(controller, action, (argument, value))));
    }

    public static TheoryData<string, string, string, object> UsesOfAPlaySession => new()
    {
        { "DynamicHls", "GetHlsVideoSegment", "playSessionId", "local-play" },
        { "Videos", "GetVideoStream", "params", ";;;;;;;;;;;;;;;;;;;;local-play" },
        { "Playstate", "ReportPlaybackStart", "playbackStartInfo", new MediaBrowser.Model.Session.PlaybackStartInfo { PlaySessionId = "local-play" } },
        { "Playstate", "ReportPlaybackProgress", "playbackProgressInfo", new MediaBrowser.Model.Session.PlaybackProgressInfo { PlaySessionId = "local-play" } },
        { "Playstate", "OnPlaybackProgress", "playSessionId", "LOCAL-PLAY" },
        { "Playstate", "PingPlaybackSession", "playSessionId", "local-play" }
    };

    [Theory]
    [MemberData(nameof(UsesOfAPlaySession))]
    public async Task A_local_play_session_its_user_keeps_using_does_not_go_idle(string controller, string action, string argument, object value)
    {
        CanSeeItem();
        var clock = new ManualClock();
        var sessions = new PlaybackSessionStore(clock);
        sessions.RecordLocal(_user.Id, "local-play", _itemId);

        clock.Advance(Retention - TimeSpan.FromSeconds(1));
        Assert.True(await RunAsync(GuardFilter(sessions), Context(controller, action, ("itemId", _itemId), (argument, value))));
        clock.Advance(TimeSpan.FromSeconds(1));
        sessions.ForgetUnusedLocal(RetentionTimes);
        Assert.True(sessions.IsLocalOfAnother(Guid.NewGuid(), "local-play"));

        clock.Advance(Retention);
        sessions.ForgetUnusedLocal(RetentionTimes);
        Assert.False(sessions.IsLocalOfAnother(Guid.NewGuid(), "local-play"));
    }

    [Fact]
    public async Task Another_user_naming_a_local_play_session_does_not_keep_it()
    {
        var clock = new ManualClock();
        var sessions = new PlaybackSessionStore(clock);
        var owner = Guid.NewGuid();
        sessions.RecordLocal(owner, "local-play", _itemId);

        clock.Advance(Retention - TimeSpan.FromSeconds(1));
        await RunAsync(GuardFilter(sessions), Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("playSessionId", "local-play")));
        clock.Advance(TimeSpan.FromSeconds(1));
        sessions.ForgetUnusedLocal(RetentionTimes);

        Assert.False(sessions.IsLocalOfAnother(_user.Id, "local-play"));
    }

    [Theory]
    [InlineData("GetHlsVideoSegment", "deviceId", "owner-device")]
    [InlineData("GetHlsVideoSegment", "deviceId", "OWNER-DEVICE")]
    [InlineData("GetHlsVideoSegment", "params", ";owner-device")]
    [InlineData("GetHlsAudioSegment", "params", ";OWNER-DEVICE")]
    public async Task A_segment_without_a_play_session_on_the_device_of_another_local_play_session_is_not_found(string action, string argument, string device)
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        var context = Context("DynamicHls", action, ("itemId", LocalItem()), (argument, device));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task The_device_of_a_segment_is_the_one_in_the_legacy_params_over_the_query()
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        var refused = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", "mine"), ("params", ";owner-device"));
        var served = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", "owner-device"), ("params", ";mine"));
        var blank = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", "owner-device"), ("params", "; ;"));

        Assert.False(await RunAsync(GuardFilter(), refused));
        Assert.True(await RunAsync(GuardFilter(), served));
        Assert.False(await RunAsync(GuardFilter(), blank));
    }

    [Fact]
    public async Task A_blank_device_in_the_legacy_params_leaves_the_device_of_a_peer_in_the_query()
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", "consumer-device"), ("params", "; ;"));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_segment_without_a_play_session_on_the_device_of_a_local_play_session_is_not_found()
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        Anonymous();
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", "owner-device"));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("device", null)]
    [InlineData("unknown-device", null)]
    [InlineData("owner-device", "a-play-session")]
    public async Task A_segment_that_stops_no_local_play_session_of_another_user_passes(string device, string? playSessionId)
    {
        _fixture.Sessions.RecordLocal(_user.Id, "own-play", _itemId, "device");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", device), ("playSessionId", playSessionId));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Theory]
    [InlineData("GetMasterHlsVideoPlaylist", "deviceId", "owner-device")]
    [InlineData("GetVariantHlsVideoPlaylist", "params", ";OWNER-DEVICE")]
    [InlineData("GetMasterHlsAudioPlaylist", "deviceId", "consumer-device")]
    [InlineData("GetVariantHlsAudioPlaylist", "deviceId", null)]
    [InlineData("GetLiveHlsStream", "deviceId", "owner-device")]
    public async Task A_playlist_without_a_play_session_passes_on_the_device_of_another_play_session(string action, string argument, string? device)
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        PeerPlays(Guid.NewGuid(), "consumer-device");
        using var job = LiveJob("local-play", null);
        var context = Context("DynamicHls", action, ("itemId", LocalItem()), (argument, device));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Fact]
    public async Task A_segment_whose_play_session_is_blank_counts_as_one_without()
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", "owner-device"), ("playSessionId", " "));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    // A live stock job of a play session.
    private TranscodingJob LiveJob(string playSessionId, string? device)
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = playSessionId, DeviceId = device };
        _transcodes.Setup(manager => manager.GetTranscodingJob(playSessionId)).Returns(job);
        return job;
    }

    [Theory]
    [InlineData("GetHlsVideoSegment", "deviceId", null, null)]
    [InlineData("GetHlsVideoSegment", "deviceId", "", "")]
    [InlineData("GetHlsVideoSegment", "deviceId", " ", null)]
    [InlineData("GetHlsAudioSegment", "params", "; ;", " ")]
    public async Task A_segment_without_a_play_session_or_a_device_is_not_found_while_another_user_has_a_job_without_a_device(string action, string argument, string? device, string? jobDevice)
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        using var job = LiveJob("local-play", jobDevice);
        var context = Context("DynamicHls", action, ("itemId", LocalItem()), (argument, device));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_segment_without_a_play_session_or_a_device_is_not_found_while_a_user_has_a_job_without_a_device()
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        using var job = LiveJob("local-play", null);
        Anonymous();
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("GetHlsVideoSegment", "deviceId", null)]
    [InlineData("GetHlsAudioSegment", "params", "; ;")]
    public async Task A_segment_without_a_play_session_or_a_device_passes_while_the_jobs_of_another_user_have_a_device(string action, string argument, string? device)
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "ended-play", _itemId);
        using var job = LiveJob("local-play", "owner-device");
        var context = Context("DynamicHls", action, ("itemId", LocalItem()), (argument, device));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Fact]
    public async Task An_anonymous_segment_without_a_play_session_or_a_device_passes_while_no_job_of_a_user_is_alive()
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        Anonymous();
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Fact]
    public async Task A_segment_without_a_play_session_or_a_device_passes_while_no_one_plays_here()
    {
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Fact]
    public async Task A_segment_without_a_play_session_or_a_device_passes_while_only_its_user_has_a_job_without_a_device()
    {
        _fixture.Sessions.RecordLocal(_user.Id, "own-play", _itemId);
        using var job = LiveJob("own-play", null);
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Fact]
    public async Task A_request_without_a_device_that_names_a_play_session_passes_while_another_user_plays_here()
    {
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId, "owner-device");
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("playSessionId", "a-play-session"));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    // A peer play session, as the PlaybackInfo of a peer confirms it on the provider.
    private void PeerPlays(Guid peer, params string[] devices)
    {
        var reservation = _peerSlots.TryReserve(peer, 1, null)!;
        _peerSlots.Confirm(peer, reservation, "peer-play", _itemId, devices);
    }

    [Theory]
    [InlineData("GetHlsVideoSegment", "deviceId", "consumer-device")]
    [InlineData("GetHlsVideoSegment", "deviceId", "CONSUMER-DEVICE")]
    [InlineData("GetHlsVideoSegment", "params", ";consumer-device")]
    [InlineData("GetHlsAudioSegment", "deviceId", "url-device")]
    public async Task A_request_without_a_play_session_on_the_device_of_a_peer_play_session_is_not_found(string action, string argument, string device)
    {
        PeerPlays(Guid.NewGuid(), "consumer-device", "url-device");
        var context = Context("DynamicHls", action, ("itemId", LocalItem()), (argument, device));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_request_without_a_play_session_on_the_device_of_a_peer_play_session_is_not_found()
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        Anonymous();
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", "consumer-device"));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_segment_without_a_play_session_or_a_device_is_not_found_while_a_peer_has_a_job_without_a_device()
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        using var job = LiveJob("peer-play", null);
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_segment_without_a_play_session_or_a_device_passes_while_the_job_of_a_peer_has_a_device()
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        using var job = LiveJob("peer-play", "jellymesh-device");
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Theory]
    [InlineData("consumer-device")]
    [InlineData(null)]
    public async Task A_request_of_a_peer_without_a_play_session_on_its_own_devices_passes(string? device)
    {
        PeerPlays(_user.Id, "consumer-device");
        using var job = LiveJob("peer-play", null);
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", device));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Theory]
    [InlineData("unknown-device", null)]
    [InlineData("consumer-device", "a-play-session")]
    public async Task A_request_that_stops_no_peer_play_session_passes(string device, string? playSessionId)
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("deviceId", device), ("playSessionId", playSessionId));

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Theory]
    [InlineData("HlsSegment", "StopEncodingProcess", "playSessionId", "peer-play")]
    [InlineData("HlsSegment", "StopEncodingProcess", "playSessionId", "PEER-PLAY")]
    [InlineData("Playstate", "OnPlaybackStopped", "playSessionId", "peer-play")]
    [InlineData("Playstate", "OnPlaybackStoppedLegacy", "playSessionId", "peer-play")]
    public async Task A_local_user_cannot_stop_a_peer_play_session(string controller, string action, string argument, string value)
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        var context = Context(controller, action, (argument, value));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Fact]
    public async Task A_local_user_cannot_report_a_peer_play_session_as_stopped()
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        var context = Context("Playstate", "ReportPlaybackStopped", ("playbackStopInfo", new MediaBrowser.Model.Session.PlaybackStopInfo { PlaySessionId = "PEER-PLAY" }));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.IsType<NoContentResult>(context.Result);
    }

    public static TheoryData<string, string, string, object> ReportsOfAPeerPlaySession => new()
    {
        { "Playstate", "ReportPlaybackStart", "playbackStartInfo", new MediaBrowser.Model.Session.PlaybackStartInfo { PlaySessionId = "peer-play" } },
        { "Playstate", "OnPlaybackStart", "playSessionId", "peer-play" },
        { "Playstate", "OnPlaybackStartLegacy", "playSessionId", "peer-play" },
        { "Playstate", "ReportPlaybackProgress", "playbackProgressInfo", new MediaBrowser.Model.Session.PlaybackProgressInfo { PlaySessionId = "PEER-PLAY" } },
        { "Playstate", "OnPlaybackProgress", "playSessionId", "peer-play" },
        { "Playstate", "OnPlaybackProgressLegacy", "playSessionId", "peer-play" },
        { "Playstate", "PingPlaybackSession", "playSessionId", "peer-play" }
    };

    [Theory]
    [MemberData(nameof(ReportsOfAPeerPlaySession))]
    public async Task A_report_of_a_local_user_on_a_peer_play_session_is_answered_without_effect(string controller, string action, string argument, object value)
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        var context = Context(controller, action, (argument, value));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Theory]
    [InlineData("DynamicHls", "GetHlsVideoSegment", "playSessionId", "peer-play")]
    [InlineData("DynamicHls", "GetHlsVideoSegment", "playSessionId", "PEER-PLAY")]
    [InlineData("DynamicHls", "GetHlsVideoSegment", "params", ";;;;;;;;;;;;;;;;;;;;peer-play")]
    [InlineData("DynamicHls", "GetHlsAudioSegment", "playSessionId", "peer-play")]
    [InlineData("DynamicHls", "GetVariantHlsVideoPlaylist", "playSessionId", "peer-play")]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist", "playSessionId", "peer-play")]
    [InlineData("Videos", "GetVideoStream", "playSessionId", "peer-play")]
    [InlineData("Videos", "GetVideoStream", "params", ";;;;;;;;;;;;;;;;;;;;peer-play")]
    [InlineData("Audio", "GetAudioStream", "playSessionId", "peer-play")]
    public async Task A_stream_of_a_local_user_cannot_name_a_peer_play_session(string controller, string action, string argument, string value)
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        var context = Context(controller, action, ("itemId", LocalItem()), (argument, value));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stream_that_names_a_peer_play_session_wants_a_user()
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");
        Anonymous();
        var context = Context("DynamicHls", "GetHlsVideoSegment", ("itemId", LocalItem()), ("playSessionId", "peer-play"));

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("HlsSegment", "StopEncodingProcess", "playSessionId", "peer-play")]
    [InlineData("Playstate", "OnPlaybackProgress", "playSessionId", "peer-play")]
    [InlineData("DynamicHls", "GetHlsVideoSegment", "playSessionId", "PEER-PLAY")]
    [InlineData("DynamicHls", "GetHlsVideoSegment", "params", ";;;;;;;;;;;;;;;;;;;;peer-play")]
    [InlineData("Videos", "GetVideoStream", "playSessionId", "peer-play")]
    public async Task The_peer_uses_its_own_play_session(string controller, string action, string argument, string value)
    {
        PeerPlays(_user.Id, "consumer-device");

        Assert.True(await RunAsync(GuardFilter(), Context(controller, action, ("itemId", LocalItem()), (argument, value))));
    }

    [Theory]
    [InlineData("HlsSegment", "StopEncodingProcess")]
    [InlineData("Playstate", "OnPlaybackStopped")]
    [InlineData("Playstate", "OnPlaybackProgress")]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    [InlineData("Videos", "GetVideoStream")]
    public async Task A_play_session_no_peer_recorded_stays_with_stock(string controller, string action)
    {
        PeerPlays(Guid.NewGuid(), "consumer-device");

        Assert.True(await RunAsync(GuardFilter(), Context(controller, action, ("itemId", LocalItem()), ("playSessionId", "real-title"))));
    }

    [Fact]
    public async Task A_local_play_session_is_recorded_with_the_device_its_transcoding_url_names()
    {
        Offline();
        LocalCopyArrived();
        var source = LocalFile(directPlay: false);
        source.TranscodingUrl += "&DeviceId=Ab+Cd%2B&DeviceId=second";
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId), ("mediaSourceId", _itemId.ToString("N")));

        await RunStockAsync(context, source);

        Assert.True(_fixture.Sessions.IsLocalDeviceOfAnother(Guid.NewGuid(), "ab cd+"));
        Assert.True(_fixture.Sessions.IsLocalDeviceOfAnother(Guid.NewGuid(), "device"));
        Assert.False(_fixture.Sessions.IsLocalDeviceOfAnother(Guid.NewGuid(), "second"));
    }

    [Fact]
    public async Task A_local_play_session_is_recorded_with_the_device_that_opened_it()
    {
        Offline();
        LocalCopyArrived();
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId), ("mediaSourceId", _itemId.ToString("N")));

        await RunStockAsync(context, LocalFile(directPlay: true));

        Assert.True(_fixture.Sessions.IsLocalDeviceOfAnother(Guid.NewGuid(), "DEVICE"));
        Assert.False(_fixture.Sessions.IsLocalDeviceOfAnother(_user.Id, "device"));
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

    // D9: the legacy segment route serves any file of the transcode folder by name, whatever item its route names.
    private const string JobHash = "0123456789abcdef0123456789abcdef";

    // A refused file reaches stock under a name that is no file, so that stock answers as for any missing one.
    private static void AssertReadsAsMissing(ActionExecutingContext context, bool passed, string argument)
    {
        Assert.True(passed);
        Assert.Null(context.Result);
        Assert.StartsWith(StockRouteGuardFilter.MissingFilePrefix, Assert.IsType<string>(context.ActionArguments[argument]), StringComparison.Ordinal);
    }

    private TranscodingJob JobOf(string playSessionId)
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = playSessionId, Path = "/cache/transcodes/" + JobHash + ".m3u8" };
        _transcodes.Setup(manager => manager.GetTranscodingJob(playSessionId)).Returns(job);
        return job;
    }

    private static ActionExecutingContext LegacySegment(Guid item, string playlistId, string segmentId) =>
        Context("HlsSegment", "GetHlsVideoSegmentLegacy", ("itemId", item.ToString("N")), ("playlistId", playlistId), ("segmentId", segmentId), ("segmentContainer", "ts"));

    [Fact]
    public async Task A_legacy_segment_wants_a_user()
    {
        Anonymous();
        var context = LegacySegment(LocalItem(), "anything", "anything0");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("other", JobHash + "3")]
    [InlineData("other", "0123456789ABCDEF0123456789ABCDEF-1")]
    [InlineData(JobHash, "other3")]
    [InlineData("6789ABCDEF", "other3")]
    public async Task A_legacy_segment_of_the_local_play_session_of_another_user_is_not_found(string playlistId, string segmentId)
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = LegacySegment(LocalItem(), playlistId, segmentId);

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task A_legacy_segment_of_the_local_play_session_of_another_user_is_not_found_when_its_stub_is_gone()
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        StubGone();
        var context = LegacySegment(LocalItem(), JobHash, JobHash + "0");

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task A_legacy_segment_of_the_play_session_of_a_peer_is_not_found()
    {
        using var job = JobOf("peer-play");
        PeerPlays(Guid.NewGuid());
        var context = LegacySegment(LocalItem(), JobHash, JobHash + "0");

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task Its_user_reads_a_legacy_segment_of_their_local_play_session()
    {
        CanSeeItem();
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), LegacySegment(_itemId, JobHash, JobHash + "0")));
    }

    [Fact]
    public async Task A_legacy_segment_of_their_local_play_session_on_a_stub_they_no_longer_see_is_not_found()
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);
        var context = LegacySegment(LocalItem(), JobHash, JobHash + "0");

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task A_legacy_segment_that_names_a_stub_the_user_cannot_see_is_not_found()
    {
        var context = LegacySegment(_itemId, "other", "other0");

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    // A playlist id that only part of the path of the job holds is refused as one that matches nothing, so growing it one
    // digit at a time tells nothing of the name of the job.
    [Theory]
    [InlineData("01")]
    [InlineData("cdef0123")]
    [InlineData("/cache/transcodes/0")]
    public async Task A_short_playlist_id_of_the_job_of_another_user_reads_as_one_of_no_file(string playlistId)
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = LegacySegment(LocalItem(), playlistId, "none");

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task A_short_playlist_id_of_no_job_goes_to_stock_untouched()
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = LegacySegment(LocalItem(), "9f", "none");

        Assert.True(await RunAsync(GuardFilter(), context));
        Assert.Equal("9f", context.ActionArguments["playlistId"]);
    }

    [Theory]
    [InlineData("f0.ts", true)]
    [InlineData("ef12", true)]
    [InlineData("f9.ts", false)]
    public async Task A_playlist_id_that_runs_into_a_segment_name_of_the_job_of_another_user_reads_as_one_of_no_file(string playlistId, bool refused)
    {
        var folder = Directory.CreateTempSubdirectory("jellymesh-transcodes-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, JobHash + "0.ts"), string.Empty);
            await File.WriteAllTextAsync(Path.Combine(folder, JobHash + "12.ts"), string.Empty);
            JobOf("local-play").Path = Path.Combine(folder, JobHash + ".m3u8");
            _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
            var context = LegacySegment(LocalItem(), playlistId, "none");

            var passed = await RunAsync(GuardFilter(), context);

            if (refused)
            {
                AssertReadsAsMissing(context, passed, "playlistId");
            }
            else
            {
                Assert.True(passed);
                Assert.Equal(playlistId, context.ActionArguments["playlistId"]);
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task A_legacy_segment_lists_the_transcode_folder_once_whatever_the_jobs_in_it()
    {
        var listings = new List<string>();
        foreach (var number in new[] { "1", "2", "3" })
        {
            var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { PlaySessionId = "play-" + number, Path = "/cache/transcodes/" + new string(number[0], 32) + ".m3u8" };
            _transcodes.Setup(manager => manager.GetTranscodingJob("play-" + number)).Returns(job);
            _fixture.Sessions.RecordLocal(Guid.NewGuid(), "play-" + number, _itemId);
        }

        var context = LegacySegment(LocalItem(), "22220.ts", "none");
        var passed = await RunAsync(GuardFilter(listFolder: folder =>
        {
            listings.Add(folder);
            return [folder + "/" + new string('2', 32) + "0.ts"];
        }), context);

        AssertReadsAsMissing(context, passed, "playlistId");
        Assert.Equal(["/cache/transcodes"], listings);
    }

    [Fact]
    public async Task A_legacy_segment_of_a_job_nobody_recorded_stays_with_stock()
    {
        using var job = JobOf("real-title");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), LegacySegment(LocalItem(), JobHash, JobHash + "0")));
    }

    // Row 212: the legacy audio segment and playlist routes serve a file of the transcode folder by its name alone.
    private static ActionExecutingContext LegacyAudio(Guid item, string segmentId, string extension = "aac")
    {
        var context = Context("HlsSegment", "GetHlsAudioSegmentLegacy", ("itemId", item.ToString("N")), ("segmentId", segmentId));
        context.HttpContext.Request.Path = "/Audio/" + item.ToString("N") + "/hls/" + segmentId + "/stream." + extension;
        return context;
    }

    private static ActionExecutingContext LegacyPlaylist(Guid item, string playlistId)
    {
        var context = Context("HlsSegment", "GetHlsPlaylistLegacy", ("itemId", item.ToString("N")), ("playlistId", playlistId));
        context.HttpContext.Request.Path = "/Videos/" + item.ToString("N") + "/hls/" + playlistId + "/stream.m3u8";
        return context;
    }

    [Fact]
    public async Task A_legacy_audio_segment_wants_a_user_whatever_file_it_names()
    {
        Anonymous();
        var context = LegacyAudio(LocalItem(), "anything");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData(JobHash, "aac")]
    [InlineData(JobHash, "mp3")]
    [InlineData(JobHash + "3", "aac")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", "mp3")]
    public async Task A_legacy_audio_segment_of_the_local_play_session_of_another_user_is_not_found(string segmentId, string extension)
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = LegacyAudio(LocalItem(), segmentId, extension);

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "segmentId");
    }

    [Fact]
    public async Task A_legacy_audio_segment_of_the_play_session_of_a_peer_is_not_found()
    {
        using var job = JobOf("peer-play");
        PeerPlays(Guid.NewGuid());
        var context = LegacyAudio(LocalItem(), JobHash);

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "segmentId");
    }

    [Fact]
    public async Task Its_user_reads_a_legacy_audio_segment_of_their_local_play_session()
    {
        CanSeeItem();
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), LegacyAudio(_itemId, JobHash)));
    }

    [Fact]
    public async Task A_legacy_audio_segment_that_names_a_stub_the_user_cannot_see_is_not_found()
    {
        var context = LegacyAudio(_itemId, "other");

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "segmentId");
    }

    [Theory]
    [InlineData("other")]
    [InlineData("6789abcdef")]
    public async Task A_legacy_audio_segment_of_a_file_nobody_recorded_stays_with_stock(string segmentId)
    {
        using var job = JobOf("real-title");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), LegacyAudio(LocalItem(), segmentId)));
    }

    [Theory]
    [InlineData(JobHash)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData(JobHash + "_subtitles")]
    public async Task A_legacy_playlist_of_the_local_play_session_of_another_user_is_not_found(string playlistId)
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = LegacyPlaylist(LocalItem(), playlistId);

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task A_legacy_playlist_of_the_local_play_session_of_another_user_is_not_found_when_its_stub_is_gone()
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        StubGone();
        var context = LegacyPlaylist(LocalItem(), JobHash);

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task A_legacy_playlist_of_the_play_session_of_a_peer_is_not_found()
    {
        using var job = JobOf("peer-play");
        PeerPlays(Guid.NewGuid());
        var context = LegacyPlaylist(LocalItem(), JobHash);

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task A_legacy_playlist_of_a_recorded_play_session_wants_a_user()
    {
        Anonymous();
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);
        var context = LegacyPlaylist(LocalItem(), JobHash);

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task Its_user_reads_the_legacy_playlist_of_their_local_play_session()
    {
        CanSeeItem();
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), LegacyPlaylist(_itemId, JobHash)));
    }

    [Fact]
    public async Task A_legacy_playlist_of_their_local_play_session_on_a_stub_they_no_longer_see_is_not_found()
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _itemId);
        var context = LegacyPlaylist(LocalItem(), JobHash);

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    [Fact]
    public async Task A_legacy_playlist_that_names_a_stub_the_user_cannot_see_is_not_found()
    {
        var context = LegacyPlaylist(_itemId, "other");

        AssertReadsAsMissing(context, await RunAsync(GuardFilter(), context), "playlistId");
    }

    // Stock reads the playlist by its own name, so a name that only holds the one of a job reads no file of it.
    [Theory]
    [InlineData("other")]
    [InlineData("6789abcdef")]
    [InlineData("x" + JobHash)]
    public async Task A_legacy_playlist_of_a_file_nobody_recorded_stays_with_stock(string playlistId)
    {
        using var job = JobOf("local-play");
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "local-play", _itemId);

        Assert.True(await RunAsync(GuardFilter(), LegacyPlaylist(LocalItem(), playlistId)));
    }
    private static readonly byte[] _remoteBytes = [0x1A, 0x45, 0xDF, 0xA3, 7];

    // A Download or a File of the stub, with a response body that keeps what is written.
    private static ActionExecutingContext FileRequest(string action, string? range = null)
    {
        var context = Context("Library", action, ("itemId", _itemId));
        context.HttpContext.Response.Body = new MemoryStream();
        if (range is not null)
        {
            context.HttpContext.Request.Headers.Range = range;
        }

        return context;
    }

    private static byte[] BodyOf(ActionExecutingContext context)
    {
        return ((MemoryStream)context.HttpContext.Response.Body).ToArray();
    }

    private void RemoteServes(System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK, string? contentRange = null)
    {
        _fixture.Client.Open = (request, _) => Task.FromResult(RemoteResult.Success(new RemoteContent(
            status,
            new RemoteContentHeaders("video/x-matroska", _remoteBytes.Length, contentRange, "bytes"),
            new MemoryStream(_remoteBytes))));
    }

    [Fact]
    public async Task File_of_a_visible_stub_comes_from_the_remote_and_never_from_stock()
    {
        CanSeeItem();
        RemoteServes();
        var context = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status200OK, context.HttpContext.Response.StatusCode);
        Assert.Equal("video/x-matroska", context.HttpContext.Response.ContentType);
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.StartsWith("/Videos/remote-item/stream?static=true", Assert.Single(_fixture.Client.Opened), StringComparison.Ordinal);
    }
    private void MayDownload(bool allowed)
    {
        _user.Permissions.Add(new Permission(PermissionKind.EnableContentDownloading, allowed));
    }

    [Fact]
    public async Task Download_without_the_download_permission_is_refused_and_nothing_is_read()
    {
        CanSeeItem();
        MayDownload(false);
        RemoteServes();
        var context = FileRequest("GetDownload");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(BodyOf(context));
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task Download_with_the_download_permission_comes_from_the_remote_with_its_range()
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServes(System.Net.HttpStatusCode.PartialContent, "bytes 0-4/100");
        RemoteStreamRequest? asked = null;
        var serve = _fixture.Client.Open;
        _fixture.Client.Open = (request, token) =>
        {
            asked = request;
            return serve(request, token);
        };
        var context = FileRequest("GetDownload", "bytes=0-4");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status206PartialContent, context.HttpContext.Response.StatusCode);
        Assert.Equal("bytes 0-4/100", context.HttpContext.Response.Headers.ContentRange.ToString());
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.Equal("bytes=0-4", asked?.Range);
    }

    [Fact]
    public async Task File_needs_no_download_permission()
    {
        CanSeeItem();
        MayDownload(false);
        RemoteServes();
        var context = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
    }
    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task File_and_download_of_a_stub_the_user_cannot_see_are_not_found(string action)
    {
        MayDownload(true);
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [InlineData("GetFile", RemoteFailure.Unreachable)]
    [InlineData("GetDownload", RemoteFailure.ServerError)]
    public async Task When_no_source_gives_the_file_the_answer_is_503_and_never_the_stub(string action, RemoteFailure failure)
    {
        CanSeeItem();
        MayDownload(true);
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(failure));
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(BodyOf(context));
    }

    [Fact]
    public async Task A_file_refused_for_the_stream_limit_is_answered_as_such_and_not_left_to_a_later_failure()
    {
        CanSeeItem();
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.StreamLimitReached));
        var context = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.False(_fixture.Coordinator.TakeFileRefusal(_stub.Id));
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task A_stub_the_plugin_no_longer_knows_never_reaches_stock(string action)
    {
        CanSeeItem();
        MayDownload(true);
        _stubs.Setup(stubs => stubs.Find("stub")).Returns((StubEntry?)null);
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(BodyOf(context));
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task A_stub_the_plugin_no_longer_knows_still_wants_a_user(string action)
    {
        Anonymous();
        _stubs.Setup(stubs => stubs.Find("stub")).Returns((StubEntry?)null);
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stub_the_plugin_no_longer_knows_is_not_found_for_a_user_who_cannot_see_it()
    {
        _stubs.Setup(stubs => stubs.Find("stub")).Returns((StubEntry?)null);
        var context = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task A_stub_Jellyfin_has_not_refreshed_yet_never_reaches_stock(string action)
    {
        CanSeeItem();
        MayDownload(true);
        var store = new InMemoryConfigurationStore();
        store.Configuration.FederatedFolderPath = "/federata";
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Movie { Path = "/federata/server/Film/Film (2012)/Film (2012).strm" });
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(BodyOf(context));
    }

    [Fact]
    public async Task A_strm_outside_the_federated_folder_stays_with_stock()
    {
        var store = new InMemoryConfigurationStore();
        store.Configuration.FederatedFolderPath = "/federata";
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Movie { Path = "/media/Film (2012).strm" });

        Assert.True(await RunAsync(GuardFilter(store: store), FileRequest("GetFile")));
    }

    // A track stub is an Audio item with no plugin URL; the registry knows it by its path (F54).
    private InMemoryConfigurationStore TrackStub(bool known = true)
    {
        var store = new InMemoryConfigurationStore();
        store.Configuration.FederatedFolderPath = "/federata";
        var path = "/federata/server/library/Musica/Artist/Album/01 - Song.strm";
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Path = path });
        _stubs.Setup(stubs => stubs.FindByPath(path)).Returns(known ? _stub : null);
        return store;
    }

    [Fact]
    public async Task File_of_a_visible_track_stub_comes_from_the_remote_and_never_from_stock()
    {
        CanSeeItem();
        RemoteServes();
        var context = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.StartsWith("/Videos/remote-item/stream?static=true", Assert.Single(_fixture.Client.Opened), StringComparison.Ordinal);
    }

    // Finamp downloads a track through File, which wants no download permission (D12), and seeks with a Range.
    [Fact]
    public async Task File_of_a_track_stub_keeps_its_range_without_the_download_permission()
    {
        CanSeeItem();
        MayDownload(false);
        RemoteServes(System.Net.HttpStatusCode.PartialContent, "bytes 0-4/100");
        RemoteStreamRequest? asked = null;
        var serve = _fixture.Client.Open;
        _fixture.Client.Open = (request, token) =>
        {
            asked = request;
            return serve(request, token);
        };
        var context = FileRequest("GetFile", "bytes=0-4");

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status206PartialContent, context.HttpContext.Response.StatusCode);
        Assert.Equal("bytes 0-4/100", context.HttpContext.Response.Headers.ContentRange.ToString());
        Assert.Equal("bytes=0-4", asked?.Range);
    }

    // A track or an audiobook part, with the name its .strm has.
    private InMemoryConfigurationStore AudioStub(StubKind kind)
    {
        var store = TrackStub();
        var name = kind == StubKind.AudioBook ? "Libri/Author/Story/01 - Café 1.5.strm" : "Musica/Artist/Album/01 - Song.strm";
        _stubs.Setup(stubs => stubs.FindByPath(It.IsAny<string>())).Returns(_stub with { Kind = kind, RelativePath = "server/library/" + name });
        return store;
    }

    private void RemoteServesType(string? contentType)
    {
        _fixture.Client.Open = (request, _) => Task.FromResult(RemoteResult.Success(new RemoteContent(
            System.Net.HttpStatusCode.OK,
            new RemoteContentHeaders(contentType, _remoteBytes.Length, null, "bytes"),
            new MemoryStream(_remoteBytes))));
    }

    // Row 532: the name rule of a film's Download, with the audio types.
    [Theory]
    [InlineData(StubKind.Track, "audio/mpeg", "01 - Song.mp3")]
    [InlineData(StubKind.Track, "AUDIO/FLAC; charset=binary", "01 - Song.flac")]
    [InlineData(StubKind.Track, "audio/ogg", "01 - Song.ogg")]
    [InlineData(StubKind.Track, "video/mp4", "01 - Song")]
    [InlineData(StubKind.Track, "text/html", "01 - Song")]
    [InlineData(StubKind.Track, null, "01 - Song")]
    [InlineData(StubKind.AudioBook, "audio/mp4", "01 - Café 1.5.m4a")]
    [InlineData(StubKind.AudioBook, "audio/unknown", "01 - Café 1.5")]
    public async Task Download_of_an_audio_stub_is_an_attachment_named_as_its_stub_with_the_extension_of_the_remote_file(StubKind kind, string? contentType, string name)
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServesType(contentType);
        var context = FileRequest("GetDownload");

        Assert.False(await RunAsync(GuardFilter(store: AudioStub(kind)), context));

        var headers = context.HttpContext.Response.Headers;
        var disposition = Microsoft.Net.Http.Headers.ContentDispositionHeaderValue.Parse(headers.ContentDisposition.ToString());
        Assert.Equal("attachment", disposition.DispositionType.ToString());
        Assert.Equal(name, disposition.FileNameStar.ToString());
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal(_remoteBytes, BodyOf(context));
    }

    [Theory]
    [InlineData(StubKind.Track)]
    [InlineData(StubKind.AudioBook)]
    public async Task File_of_an_audio_stub_is_not_sniffed_and_is_not_an_attachment(StubKind kind)
    {
        CanSeeItem();
        RemoteServesType("audio/mpeg");
        var context = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(store: AudioStub(kind)), context));

        Assert.Equal("nosniff", context.HttpContext.Response.Headers.XContentTypeOptions.ToString());
        Assert.Empty(context.HttpContext.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("audio/mpeg", context.HttpContext.Response.ContentType);
    }

    [Theory]
    [InlineData("HEAD", null)]
    [InlineData("GET", "bytes=0-4")]
    public async Task Head_and_a_range_of_an_audio_download_carry_the_same_headers(string method, string? range)
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServesType("audio/mpeg");
        var context = FileRequest("GetDownload", range);
        context.HttpContext.Request.Method = method;

        Assert.False(await RunAsync(GuardFilter(store: AudioStub(StubKind.AudioBook)), context));

        Assert.Equal("nosniff", context.HttpContext.Response.Headers.XContentTypeOptions.ToString());
        Assert.StartsWith("attachment", context.HttpContext.Response.Headers.ContentDisposition.ToString(), StringComparison.Ordinal);
    }

    // A static stream is the file itself, so it is not sniffed, and a player opens it without an attachment.
    [Fact]
    public async Task A_static_stream_of_an_audio_stub_is_not_sniffed_and_is_not_an_attachment()
    {
        CanSeeItem();
        RemoteServesType("audio/mpeg");
        var context = AudioRequest("Audio", "GetAudioStream", "GET", ("static", true));

        Assert.False(await RunAsync(GuardFilter(store: AudioStub(StubKind.Track)), context));

        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.Equal("nosniff", context.HttpContext.Response.Headers.XContentTypeOptions.ToString());
        Assert.Empty(context.HttpContext.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task PlaybackInfo_of_a_visible_track_stub_comes_from_the_remote()
    {
        CanSeeItem();
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(TrackStub()), context));
        var response = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(context.Result).Value);
        Assert.StartsWith("/JellyMesh/Playback/", Assert.Single(response.MediaSources).TranscodingUrl, StringComparison.Ordinal);
        Assert.Equal("remote-item", Assert.Single(_fixture.Client.Requests).ItemId);
    }

    [Fact]
    public async Task PlaybackInfo_of_a_track_stub_wants_a_user_who_sees_it()
    {
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(TrackStub()), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Requests);
    }

    // No local audio transcode: a remote that answers but cannot serve leaves a clear error, and stock is never reached.
    [Fact]
    public async Task PlaybackInfo_of_a_track_stub_the_remote_cannot_serve_is_refused_without_a_local_transcode()
    {
        CanSeeItem();
        _fixture.Client.Answer = (request, _) => Task.FromResult(RemoteResult.Success(new PlaybackInfoResponse { PlaySessionId = "remote-play", ErrorCode = PlaybackErrorCode.NotAllowed }));
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId), ("playbackInfoDto", new { DeviceProfile = PlaybackValues.WebProfile }));

        Assert.False(await RunAsync(InfoFilter(TrackStub()), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task The_segments_of_a_visible_track_stub_stay_with_stock()
    {
        CanSeeItem();

        Assert.True(await RunAsync(GuardFilter(store: TrackStub()), Context("MediaSegments", "GetItemSegments", ("itemId", _itemId))));
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task File_of_a_track_stub_wants_a_user(string action)
    {
        Anonymous();
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Theory]
    [InlineData("Audio", "GetAudioStream")]
    [InlineData("Audio", "GetAudioStreamByContainer")]
    [InlineData("UniversalAudio", "GetUniversalAudioStream")]
    [InlineData("DynamicHls", "GetMasterHlsAudioPlaylist")]
    [InlineData("DynamicHls", "GetVariantHlsAudioPlaylist")]
    public async Task A_stock_audio_route_refuses_a_track_stub_without_a_user(string controller, string action)
    {
        Anonymous();
        var context = Context(controller, action, ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_stock_audio_route_does_not_find_a_track_stub_the_user_cannot_see()
    {
        var context = Context("Audio", "GetAudioStream", ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    // Stock cannot play an audio .strm and would send its text, so even a user who sees the track never reaches it. A stock
    // segment of a track stub was never handed out: the client gets the segments of its session.
    [Theory]
    [InlineData("GetAudioStream", false)]
    [InlineData("GetHlsAudioSegment", true)]
    public async Task A_stock_audio_route_never_reaches_stock_for_a_visible_track_stub(string action, bool known)
    {
        CanSeeItem();
        var context = Context(action == "GetAudioStream" ? "Audio" : "DynamicHls", action, ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub(known)), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    private ActionExecutingContext AudioRequest(string controller, string action, string method, params (string Key, object? Value)[] arguments)
    {
        var context = Context(controller, action, new (string Key, object? Value)[] { ("itemId", _itemId) }.Concat(arguments).ToArray());
        context.HttpContext.Request.Method = method;
        context.HttpContext.Response.Body = new MemoryStream();
        return context;
    }

    // What jellyfin-web sends to universal.
    private ActionExecutingContext Universal(string method = "GET")
    {
        return AudioRequest(
            "UniversalAudio",
            "GetUniversalAudioStream",
            method,
            ("container", new[] { "opus", "mp3|mp3", "flac" }),
            ("transcodingContainer", "ts"),
            ("transcodingProtocol", Jellyfin.Data.Enums.MediaStreamProtocol.hls),
            ("audioCodec", "aac"),
            ("maxStreamingBitrate", 320000));
    }

    // What Finamp sends for HLS.
    private ActionExecutingContext FinampVariant()
    {
        return AudioRequest("DynamicHls", "GetVariantHlsAudioPlaylist", "GET", ("audioCodec", "aac"), ("segmentContainer", "mp4"), ("audioBitRate", 320000), ("playSessionId", "finamp-play"));
    }

    private static void HoldsNoRemoteValue(string text)
    {
        foreach (var secret in new[] { PlaybackValues.RemoteToken, PlaybackValues.RemoteDevice, PlaybackValues.RemotePlaySession, "remote.invalid", "remote-item" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Universal_of_a_track_stub_plays_what_the_remote_serves_as_it_is()
    {
        CanSeeItem();
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var context = Universal();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.StartsWith("/Videos/remote-item/stream?static=true", _fixture.Client.Opened[^1], StringComparison.Ordinal);
        var profile = Assert.Single(_fixture.Client.Requests).DeviceProfile!;
        Assert.Equal(["opus", "mp3", "flac"], profile.DirectPlayProfiles.Select(direct => direct.Container));
        Assert.Equal("mp3", profile.DirectPlayProfiles[1].AudioCodec);
        Assert.Equal(DlnaProfileType.Audio, profile.DirectPlayProfiles[0].Type);
        var transcoding = Assert.Single(profile.TranscodingProfiles);
        Assert.Equal(("ts", "aac", Jellyfin.Data.Enums.MediaStreamProtocol.hls), (transcoding.Container, transcoding.AudioCodec, transcoding.Protocol));
    }

    // jellyfin-web sends HEAD and then GET, and a seek asks again with a Range: one stream on the remote for all of them.
    [Fact]
    public async Task Head_and_get_of_universal_share_one_remote_stream()
    {
        CanSeeItem();
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var store = TrackStub();
        var guard = GuardFilter(store: store);

        Assert.False(await RunAsync(guard, Universal("HEAD")));
        var get = Universal();
        get.HttpContext.Request.Headers.Range = "bytes=100-";
        Assert.False(await RunAsync(guard, get));

        Assert.Single(_fixture.Client.Requests);
        Assert.Equal(_remoteBytes, BodyOf(get));
    }

    [Fact]
    public async Task Universal_the_remote_transcodes_to_hls_answers_a_master_of_the_session()
    {
        CanSeeItem();
        var context = Universal();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        var playlist = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal("application/x-mpegURL", playlist.ContentType);
        var session = Assert.Single(_fixture.Sessions.All);
        Assert.Contains("\n../../JellyMesh/Playback/" + session.Id + "/f/", playlist.Content, StringComparison.Ordinal);
        Assert.Contains("ApiKey=" + Token, playlist.Content, StringComparison.Ordinal);
        HoldsNoRemoteValue(playlist.Content!);
    }

    [Fact]
    public async Task Head_of_universal_the_remote_transcodes_to_hls_tells_a_playlist_without_a_body()
    {
        CanSeeItem();
        var context = Universal("HEAD");

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        var head = Assert.IsType<FileContentResult>(context.Result);
        Assert.Equal("application/x-mpegURL", head.ContentType);
        Assert.Empty(head.FileContents);
    }

    [Fact]
    public async Task Main_playlist_of_a_track_stub_lists_the_segments_of_its_session()
    {
        CanSeeItem();
        var context = FinampVariant();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        var playlist = Assert.IsType<ContentResult>(context.Result);
        var session = Assert.Single(_fixture.Sessions.All);
        Assert.Contains("\n../../JellyMesh/Playback/" + session.Id + "/f/", playlist.Content, StringComparison.Ordinal);
        Assert.Contains("0.mp4", playlist.Content, StringComparison.Ordinal);
        HoldsNoRemoteValue(playlist.Content!);
        var asked = Assert.Single(_fixture.Client.Requests);
        Assert.Equal((false, false, 320000), (asked.EnableDirectPlay, asked.EnableDirectStream, asked.MaxStreamingBitrate));
        var transcoding = Assert.Single(asked.DeviceProfile!.TranscodingProfiles);
        Assert.Equal(("mp4", "aac", Jellyfin.Data.Enums.MediaStreamProtocol.hls), (transcoding.Container, transcoding.AudioCodec, transcoding.Protocol));
    }

    [Fact]
    public async Task Master_playlist_of_a_track_stub_leads_to_its_session()
    {
        CanSeeItem();
        var context = AudioRequest("DynamicHls", "GetMasterHlsAudioPlaylist", "GET", ("audioCodec", "aac"));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        var playlist = Assert.IsType<ContentResult>(context.Result);
        Assert.Contains("../../JellyMesh/Playback/" + Assert.Single(_fixture.Sessions.All).Id + "/f/", playlist.Content, StringComparison.Ordinal);
        HoldsNoRemoteValue(playlist.Content!);
    }

    // A remote answers an audio transcode with an audio master: its variant holds no video codec.
    private void RemoteTranscodesAudioToHls()
    {
        _fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            answer.Value!.MediaSources[0].TranscodingUrl = PlaybackValues.AudioMaster;
            return Task.FromResult(answer);
        };
        _fixture.Client.Master = PlaybackValues.AudioMasterPlaylist;
        _fixture.Client.Variant = PlaybackValues.AudioVariantPlaylist;
    }

    [Fact]
    public async Task Universal_of_a_track_stub_with_an_audio_master_leads_to_its_session()
    {
        CanSeeItem();
        RemoteTranscodesAudioToHls();
        var context = Universal();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        var playlist = Assert.IsType<ContentResult>(context.Result);
        var session = Assert.Single(_fixture.Sessions.All);
        Assert.Equal(PlaybackStreamKind.Hls, session.Kind);
        Assert.Contains("CODECS=\"mp4a.40.2\"", playlist.Content, StringComparison.Ordinal);
        Assert.Contains("\n../../JellyMesh/Playback/" + session.Id + "/f/", playlist.Content, StringComparison.Ordinal);
        HoldsNoRemoteValue(playlist.Content!);
    }

    [Fact]
    public async Task Main_playlist_of_a_track_stub_with_an_audio_master_lists_its_segments()
    {
        CanSeeItem();
        RemoteTranscodesAudioToHls();
        var context = FinampVariant();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        var playlist = Assert.IsType<ContentResult>(context.Result).Content!;
        var prefix = "../../JellyMesh/Playback/" + Assert.Single(_fixture.Sessions.All).Id + "/f/";
        Assert.Contains("URI=\"" + prefix, playlist, StringComparison.Ordinal);
        Assert.Contains("\n" + prefix, playlist, StringComparison.Ordinal);
        Assert.Contains("0.mp4", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-ENDLIST", playlist, StringComparison.Ordinal);
        HoldsNoRemoteValue(playlist);
    }

    // A rooted variant would leave the folder of the session.
    [Fact]
    public async Task Main_playlist_of_a_master_with_a_rooted_variant_is_a_bad_gateway()
    {
        CanSeeItem();
        RemoteTranscodesAudioToHls();
        _fixture.Client.Master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\n/Audio/other/main.m3u8\n";
        var context = FinampVariant();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task Universal_of_a_track_stub_the_remote_transcodes_to_a_file_relays_it()
    {
        CanSeeItem();
        RemoteServes();
        _fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            var source = answer.Value!.MediaSources[0];
            source.TranscodingUrl = "/Audio/remote-item/stream.mp3?DeviceId=" + PlaybackValues.RemoteDevice + "&ApiKey=" + PlaybackValues.RemoteToken;
            source.TranscodingSubProtocol = Jellyfin.Data.Enums.MediaStreamProtocol.http;
            return Task.FromResult(answer);
        };
        var guard = GuardFilter(store: TrackStub());

        var head = Universal("HEAD");
        Assert.False(await RunAsync(guard, head));
        Assert.Equal("audio/mpeg", Assert.IsType<FileContentResult>(head.Result).ContentType);
        var get = Universal();
        Assert.False(await RunAsync(guard, get));

        Assert.Equal(_remoteBytes, BodyOf(get));
        Assert.Equal(PlaybackStreamKind.Progressive, Assert.Single(_fixture.Sessions.All).Kind);
        Assert.Single(_fixture.Client.Requests);
    }

    // The extension of a transcode comes from the remote, so the HEAD of universal tells an audio type only.
    [Fact]
    public async Task Head_of_universal_the_remote_transcodes_to_a_file_of_another_type_tells_an_audio_type()
    {
        CanSeeItem();
        RemoteServes();
        _fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            var source = answer.Value!.MediaSources[0];
            source.TranscodingUrl = "/Audio/remote-item/stream.html?DeviceId=" + PlaybackValues.RemoteDevice;
            source.TranscodingSubProtocol = Jellyfin.Data.Enums.MediaStreamProtocol.http;
            return Task.FromResult(answer);
        };
        var head = Universal("HEAD");

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), head));
        Assert.Equal("audio/mpeg", Assert.IsType<FileContentResult>(head.Result).ContentType);
    }

    // The same query from another user or device is another playback: it never gets the session of the first.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_same_universal_query_from_another_user_or_device_opens_its_own_session(bool otherUser)
    {
        CanSeeItem();
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var guard = GuardFilter(store: TrackStub());
        Assert.False(await RunAsync(guard, Universal()));
        var other = new User("other", "auth", "reset");
        _library.Setup(library => library.GetItemById<BaseItem>(_itemId, other)).Returns(new Movie());
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = otherUser ? other : _user, Token = Token, DeviceId = otherUser ? "device" : "another-device" });

        var second = Universal();
        Assert.False(await RunAsync(guard, second));

        Assert.Equal(2, _fixture.Client.Requests.Count);
        Assert.Equal(2, _fixture.Sessions.All.Count);
        Assert.Equal(_remoteBytes, BodyOf(second));
    }

    [Fact]
    public async Task A_session_whose_source_failed_is_not_reused()
    {
        CanSeeItem();
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var guard = GuardFilter(store: TrackStub());
        Assert.False(await RunAsync(guard, Universal()));
        Assert.True(Assert.Single(_fixture.Sessions.All).TryFail());

        Assert.False(await RunAsync(guard, Universal()));

        Assert.Equal(2, _fixture.Client.Requests.Count);
    }

    // A playlist route never answers with the bytes of a stream.
    [Fact]
    public async Task A_playlist_route_of_a_track_stub_the_remote_plays_as_it_is_is_refused()
    {
        CanSeeItem();
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var context = FinampVariant();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_static_stream_of_a_track_stub_is_its_remote_file()
    {
        CanSeeItem();
        RemoteServes();
        var context = AudioRequest("Audio", "GetAudioStream", "GET", ("static", true));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.Empty(_fixture.Client.Requests);
        Assert.StartsWith("/Videos/remote-item/stream?static=true", Assert.Single(_fixture.Client.Opened), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_of_a_track_stub_in_a_container_comes_through_a_session()
    {
        CanSeeItem();
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var context = AudioRequest("Audio", "GetAudioStreamByContainer", "GET", ("container", "mp3"), ("audioCodec", "mp3"));

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        var direct = Assert.Single(Assert.Single(_fixture.Client.Requests).DeviceProfile!.DirectPlayProfiles);
        Assert.Equal(("mp3", "mp3"), (direct.Container, direct.AudioCodec));
    }

    // No local audio transcode: a remote that answers but cannot serve leaves a clear error, and stock is never reached.
    [Fact]
    public async Task Universal_of_a_track_stub_no_source_serves_is_refused()
    {
        CanSeeItem();
        _fixture.Client.Answer = (request, _) => Task.FromResult(RemoteResult.Success(new PlaybackInfoResponse { PlaySessionId = "remote-play", ErrorCode = PlaybackErrorCode.NotAllowed }));
        var context = Universal();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Sessions.All);
    }

    private ActionExecutingContext LyricsRequest(string action = "GetLyrics")
    {
        return AudioRequest("Lyrics", action, "GET");
    }

    [Fact]
    public async Task Lyrics_of_a_track_stub_come_from_its_remote()
    {
        CanSeeItem();
        _fixture.Client.Lyrics = _ => RemoteResult.Success(new RemoteLyrics(new LyricDto { Lyrics = [new LyricLine("One", 10)] }));
        var context = LyricsRequest();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        var lyrics = Assert.IsType<LyricDto>(Assert.IsType<OkObjectResult>(context.Result).Value);
        Assert.Equal("One", Assert.Single(lyrics.Lyrics).Text);
        Assert.Equal(["remote-item"], _fixture.Client.LyricsAsked);
    }

    [Fact]
    public async Task Lyrics_the_remote_does_not_have_are_not_found()
    {
        CanSeeItem();
        _fixture.Client.Lyrics = _ => RemoteResult.Success(RemoteLyrics.None);
        var context = LyricsRequest();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.IsType<NotFoundResult>(context.Result);
    }

    // The title stays visible while its server is offline, and asking for its lyrics gets a clear error.
    [Fact]
    public async Task Lyrics_no_source_can_read_are_refused_with_a_clear_error()
    {
        CanSeeItem();
        _fixture.Client.Lyrics = _ => RemoteResult.Fail<RemoteLyrics>(RemoteFailure.Unreachable);
        var context = LyricsRequest();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    // A JellyMesh remote answers 403 on Lyrics to its peers until row 460, which means it has none to give.
    [Fact]
    public async Task Lyrics_a_jellymesh_remote_keeps_from_its_peers_are_not_found()
    {
        CanSeeItem();
        _fixture.Registry.AddJellyMesh(_stub.Sources[0].ServerId);
        _fixture.Client.Lyrics = _ => RemoteResult.Fail<RemoteLyrics>(RemoteFailure.Forbidden);
        var context = LyricsRequest();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.IsType<NotFoundResult>(context.Result);
        Assert.DoesNotContain(RemoteFailure.Forbidden, _fixture.Registry.Recorded);
    }

    [Fact]
    public async Task Lyrics_a_stock_remote_refuses_are_refused_with_a_clear_error()
    {
        CanSeeItem();
        _fixture.Client.Lyrics = _ => RemoteResult.Fail<RemoteLyrics>(RemoteFailure.Forbidden);
        var context = LyricsRequest();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Contains(RemoteFailure.Forbidden, _fixture.Registry.Recorded);
    }

    [Fact]
    public async Task Lyrics_of_a_track_stub_want_a_user()
    {
        Anonymous();
        var context = LyricsRequest();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.LyricsAsked);
    }

    [Fact]
    public async Task Lyrics_of_a_track_stub_the_user_cannot_see_are_not_found()
    {
        var context = LyricsRequest();

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.LyricsAsked);
    }

    // Stock would write the lyrics of a track stub next to its .strm, inside the federated folder.
    [Theory]
    [InlineData("UploadLyrics", true, StatusCodes.Status403Forbidden)]
    [InlineData("DeleteLyrics", true, StatusCodes.Status403Forbidden)]
    [InlineData("SearchRemoteLyrics", true, StatusCodes.Status403Forbidden)]
    [InlineData("DownloadRemoteLyrics", true, StatusCodes.Status403Forbidden)]
    [InlineData("UploadLyrics", false, StatusCodes.Status404NotFound)]
    [InlineData("DownloadRemoteLyrics", false, StatusCodes.Status404NotFound)]
    public async Task Lyrics_of_a_track_stub_are_never_managed(string action, bool sees, int status)
    {
        if (sees)
        {
            CanSeeItem();
        }

        var context = LyricsRequest(action);

        Assert.False(await RunAsync(GuardFilter(store: TrackStub()), context));
        Assert.Equal(status, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.LyricsAsked);
    }

    [Fact]
    public async Task Lyrics_of_a_local_track_stay_with_stock()
    {
        CanSeeItem();
        var context = LyricsRequest();

        Assert.True(await RunAsync(GuardFilter(), context));
    }

    [Fact]
    public async Task An_audio_file_outside_the_federated_folder_stays_with_stock()
    {
        Anonymous();
        var store = new InMemoryConfigurationStore();
        store.Configuration.FederatedFolderPath = "/federata";
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new MediaBrowser.Controller.Entities.Audio.Audio { Path = "/media/music/01 - Song.flac" });

        Assert.True(await RunAsync(GuardFilter(store: store), Context("Audio", "GetAudioStream", ("itemId", _itemId))));
        Assert.True(await RunAsync(GuardFilter(store: store), FileRequest("GetFile")));
    }

    private const string BookPath = "/federata/server/Libri/Dune (1965)/Dune (1965).epub";

    // A book placeholder of the federated folder, its stub found by its path, as a book has no URL to name it.
    private InMemoryConfigurationStore BookPlaceholder(bool known = true)
    {
        var store = new InMemoryConfigurationStore();
        store.Configuration.FederatedFolderPath = "/federata";
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Book { Path = BookPath });
        _stubs.Setup(stubs => stubs.FindByPath(BookPath)).Returns(known ? _stub with { Kind = StubKind.Book, RelativePath = "server/Libri/Dune (1965)/Dune (1965).epub" } : null);
        return store;
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task A_book_comes_from_the_remote_file_with_its_range_and_never_from_the_placeholder(string action)
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServes(System.Net.HttpStatusCode.PartialContent, "bytes 0-4/100");
        RemoteStreamRequest? asked = null;
        var serve = _fixture.Client.Open;
        _fixture.Client.Open = (request, token) =>
        {
            asked = request;
            return serve(request, token);
        };
        var context = FileRequest(action, "bytes=0-4");

        Assert.False(await RunAsync(GuardFilter(store: BookPlaceholder()), context));
        Assert.Equal(StatusCodes.Status206PartialContent, context.HttpContext.Response.StatusCode);
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.Equal("/Items/remote-item/File", Assert.Single(_fixture.Client.Opened));
        Assert.Equal("bytes=0-4", asked?.Range);
    }

    // The type comes from the format of the placeholder, never from the remote, which says video/x-matroska here.
    [Theory]
    [InlineData("GetFile", "epub", "application/epub+zip")]
    [InlineData("GetDownload", "epub", "application/epub+zip")]
    [InlineData("GetFile", "pdf", "application/pdf")]
    [InlineData("GetFile", "cbz", "application/vnd.comicbook+zip")]
    [InlineData("GetFile", "cbr", "application/vnd.comicbook-rar")]
    [InlineData("GetFile", "cb7", "application/x-cb7")]
    [InlineData("GetFile", "cbt", "application/x-cbt")]
    [InlineData("GetFile", "mobi", "application/x-mobipocket-ebook")]
    [InlineData("GetFile", "azw", "application/vnd.amazon.ebook")]
    [InlineData("GetFile", "azw3", "application/vnd.amazon.ebook")]
    [InlineData("GetFile", "html", "application/octet-stream")]
    public async Task A_book_goes_with_the_media_type_of_its_format(string action, string extension, string mediaType)
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServes();
        var store = BookPlaceholder();
        _stubs.Setup(stubs => stubs.FindByPath(BookPath)).Returns(_stub with { Kind = StubKind.Book, RelativePath = "server/Libri/Dune (1965)/Dune (1965)." + extension });
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(mediaType, context.HttpContext.Response.ContentType);
        Assert.Equal(_remoteBytes, BodyOf(context));
    }

    [Fact]
    public async Task A_book_is_an_attachment_with_its_local_name_only_on_Download()
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServes();
        var store = BookPlaceholder();
        var download = FileRequest("GetDownload");
        var file = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(store: store), download));
        Assert.False(await RunAsync(GuardFilter(store: store), file));

        var disposition = download.HttpContext.Response.Headers.ContentDisposition.ToString();
        Assert.StartsWith("attachment", disposition, StringComparison.Ordinal);
        Assert.Contains("Dune (1965).epub", disposition, StringComparison.Ordinal);
        Assert.Equal("nosniff", download.HttpContext.Response.Headers.XContentTypeOptions.ToString());
        Assert.Empty(file.HttpContext.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("nosniff", file.HttpContext.Response.Headers.XContentTypeOptions.ToString());
    }

    // D63: a book is never played, so its PlaybackInfo is refused here and no remote is called.
    [Fact]
    public async Task PlaybackInfo_of_a_book_is_refused_here_with_a_text_and_reaches_no_remote()
    {
        CanSeeItem();
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(BookPlaceholder()), context));

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal(PlaybackRefusal.BookText, result.Content);
        Assert.Empty(_fixture.Client.Requests);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task PlaybackInfo_of_a_book_the_user_cannot_see_is_not_found()
    {
        var context = Context("MediaInfo", "GetPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(BookPlaceholder()), context));

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Requests);
    }

    [Theory]
    [InlineData("Videos", "GetVideoStream")]
    [InlineData("Audio", "GetAudioStream")]
    [InlineData("DynamicHls", "GetMasterHlsVideoPlaylist")]
    [InlineData("DynamicHls", "GetHlsVideoSegment")]
    [InlineData("MediaSegments", "GetItemSegments")]
    public async Task A_stream_or_segment_of_a_book_is_refused_here_with_a_text(string controller, string action)
    {
        CanSeeItem();
        var context = Context(controller, action, ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(store: BookPlaceholder()), context));

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal(PlaybackRefusal.BookText, result.Content);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task A_stream_of_a_book_the_user_cannot_see_is_not_found()
    {
        var context = Context("Videos", "GetVideoStream", ("itemId", _itemId));

        Assert.False(await RunAsync(GuardFilter(store: BookPlaceholder()), context));

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task A_book_needs_the_download_permission_only_on_Download()
    {
        CanSeeItem();
        MayDownload(false);
        RemoteServes();
        var store = BookPlaceholder();
        var download = FileRequest("GetDownload");
        var file = FileRequest("GetFile");

        Assert.False(await RunAsync(GuardFilter(store: store), download));
        Assert.False(await RunAsync(GuardFilter(store: store), file));

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(download.Result).StatusCode);
        Assert.Equal(_remoteBytes, BodyOf(file));
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task A_book_the_user_cannot_see_is_not_found(string action)
    {
        MayDownload(true);
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(store: BookPlaceholder()), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    // Jellyfin shows an item in no library to everyone, so a book the user sees only that way is not found (D17).
    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task A_visible_book_in_no_library_is_not_found(string action)
    {
        CanSeeItem();
        MayDownload(true);
        RemoteServes();
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(store: BookPlaceholder()), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
        Assert.Empty(BodyOf(context));
    }

    [Theory]
    [InlineData("GetFile", true)]
    [InlineData("GetDownload", true)]
    [InlineData("GetFile", false)]
    public async Task A_book_whose_remote_is_offline_is_a_503_that_says_so(string action, bool known)
    {
        CanSeeItem();
        MayDownload(true);
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.Unreachable));
        var context = FileRequest(action);

        Assert.False(await RunAsync(GuardFilter(store: BookPlaceholder(known)), context));
        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(PlaybackRefusal.FileOfflineText, result.Content);
        Assert.Empty(BodyOf(context));
    }

    [Fact]
    public async Task A_book_outside_the_federated_folder_stays_with_stock()
    {
        var store = BookPlaceholder();
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Book { Path = "/media/Libri/Dune.epub" });

        Assert.True(await RunAsync(GuardFilter(store: store), FileRequest("GetFile")));
    }

    [Fact]
    public async Task File_of_an_item_that_is_not_a_stub_stays_with_stock()
    {
        Assert.True(await RunAsync(GuardFilter(), Context("Library", "GetFile", ("itemId", LocalItem()))));
    }
}
