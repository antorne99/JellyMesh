using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class PlaybackProxyTests
{
    private static readonly Guid _item = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly CoordinatorFixture _fixture = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly CompletionFeature _response = new();
    private readonly PlaybackProxyController _controller;

    public PlaybackProxyTests()
    {
        _library.Setup(library => library.GetItemById<BaseItem>(_item, _user)).Returns(new Movie());
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = _user, Token = PlaybackValues.LocalToken, DeviceId = "device" });
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(_response);
        _controller = new PlaybackProxyController(
            _fixture.Sessions,
            _fixture.Coordinator,
            new StubLookup(_library.Object, _stubs.Object, new InMemoryConfigurationStore()),
            _authorization.Object,
            _fixture.Clock,
            new PlaybackRefusal(_sessionManager.Object, NullLogger<PlaybackRefusal>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private PlaybackSession Session(PlaybackStreamKind kind)
    {
        var identity = new PlaybackIdentity(_user.Id, "device", _item, "stub");
        var session = new PlaybackSession(identity, PlaybackValues.Source(), PlaybackValues.RemotePlaySession, kind, PlaybackUrls.WithoutToken(PlaybackValues.Master), _fixture.Clock.GetUtcNow());
        _fixture.Sessions.Add(session);
        return session;
    }

    private void StreamsFailWith(RemoteFailure failure)
    {
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(failure));
    }

    private void VerifyMessage(Times times)
    {
        _sessionManager.Verify(
            manager => manager.SendMessageToUserDeviceSessions(
                "device",
                SessionMessageType.GeneralCommand,
                It.Is<MediaBrowser.Model.Session.GeneralCommand>(command => command.Arguments["Text"] == PlaybackRefusal.LimitText),
                It.IsAny<CancellationToken>()),
            times);
    }

    [Fact]
    public async Task A_stream_refused_for_the_limit_gets_the_503_and_the_message_on_the_device()
    {
        var session = Session(PlaybackStreamKind.Direct);
        StreamsFailWith(RemoteFailure.StreamLimitReached);

        var result = await _controller.GetStream(session.Id, CancellationToken.None);
        await _response.CompleteAsync();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        VerifyMessage(Times.Once());
    }

    [Fact]
    public async Task A_playlist_refused_for_the_limit_gets_the_503_and_the_message_on_the_device()
    {
        var session = Session(PlaybackStreamKind.Hls);
        _fixture.Client.Playlists = _ => RemoteFailure.StreamLimitReached;

        var result = await _controller.GetMaster(session.Id, CancellationToken.None);
        await _response.CompleteAsync();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(result).StatusCode);
        VerifyMessage(Times.Once());
    }

    [Fact]
    public async Task The_refusal_leaves_the_session_so_that_the_same_url_plays_later()
    {
        var session = Session(PlaybackStreamKind.Direct);
        StreamsFailWith(RemoteFailure.StreamLimitReached);

        await _controller.GetStream(session.Id, CancellationToken.None);

        Assert.Same(session, Assert.Single(_fixture.Sessions.All));
    }

    [Theory]
    [InlineData(RemoteFailure.Unreachable)]
    [InlineData(RemoteFailure.Timeout)]
    [InlineData(RemoteFailure.Forbidden)]
    [InlineData(RemoteFailure.ServerError)]
    [InlineData(RemoteFailure.Busy)]
    public async Task Any_other_failure_of_a_stream_stays_a_bare_bad_gateway(RemoteFailure failure)
    {
        var session = Session(PlaybackStreamKind.Direct);
        StreamsFailWith(failure);

        var result = await _controller.GetStream(session.Id, CancellationToken.None);
        await _response.CompleteAsync();

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
        VerifyMessage(Times.Never());
    }

    [Theory]
    [InlineData(RemoteFailure.Unreachable)]
    [InlineData(RemoteFailure.ServerError)]
    public async Task Any_other_failure_of_a_playlist_stays_a_bare_bad_gateway(RemoteFailure failure)
    {
        var session = Session(PlaybackStreamKind.Hls);
        _fixture.Client.Playlists = _ => failure;

        var result = await _controller.GetMaster(session.Id, CancellationToken.None);
        await _response.CompleteAsync();

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
        VerifyMessage(Times.Never());
    }

    // Row 229: the title of the session is a stub whose path left every library after the session opened.
    private void StubLeftEveryLibrary()
    {
        _library.Setup(library => library.GetItemById(_item)).Returns(new Movie { Id = _item, ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "stub", "signature") });
        _stubs.Setup(stubs => stubs.Find("stub")).Returns(PlaybackValues.Stub(PlaybackValues.Source()));
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
    }

    [Fact]
    public async Task A_session_whose_stub_left_every_library_stops_streaming_to_its_owner()
    {
        var session = Session(PlaybackStreamKind.Direct);
        StubLeftEveryLibrary();

        Assert.IsType<NotFoundResult>(await _controller.GetStream(session.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_session_whose_stub_left_every_library_serves_its_owner_no_playlist()
    {
        var session = Session(PlaybackStreamKind.Hls);
        StubLeftEveryLibrary();

        Assert.IsType<NotFoundResult>(await _controller.GetMaster(session.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await _controller.GetFile(session.Id, 0, "main.m3u8", CancellationToken.None));
    }

    [Fact]
    public async Task A_session_whose_stub_left_every_library_keeps_playing_for_an_admin()
    {
        _user.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        var session = Session(PlaybackStreamKind.Direct);
        StubLeftEveryLibrary();
        StreamsFailWith(RemoteFailure.Unreachable);

        var result = await _controller.GetStream(session.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task A_session_whose_stub_is_in_a_library_streams_to_its_owner()
    {
        var session = Session(PlaybackStreamKind.Direct);
        StubLeftEveryLibrary();
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([new Folder()]);
        StreamsFailWith(RemoteFailure.Unreachable);

        var result = await _controller.GetStream(session.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task After_a_source_fails_the_session_answers_a_bad_gateway_at_once_without_asking_the_remote()
    {
        var session = Session(PlaybackStreamKind.Hls);
        var reads = 0;
        _fixture.Client.Playlists = _ => ++reads == 1 ? RemoteFailure.ServerError : RemoteFailure.None;

        await _controller.GetMaster(session.Id, CancellationToken.None);
        var result = await _controller.GetMaster(session.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task A_failed_stream_marks_the_session_and_its_retry_reaches_no_remote()
    {
        var session = Session(PlaybackStreamKind.Direct);
        StreamsFailWith(RemoteFailure.Unreachable);
        await _controller.GetStream(session.Id, CancellationToken.None);
        _fixture.Client.Opened.Clear();

        var result = await _controller.GetStream(session.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
        Assert.True(session.HasFailed);
    }

    [Fact]
    public async Task A_remote_that_drops_in_the_middle_of_a_stream_marks_the_session()
    {
        var session = Session(PlaybackStreamKind.Direct);
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Success(new RemoteContent(
            System.Net.HttpStatusCode.OK,
            new RemoteContentHeaders("video/mp4", null, null, null),
            new DroppingStream())));
        await _controller.GetStream(session.Id, CancellationToken.None);
        _fixture.Client.Opened.Clear();

        var result = await _controller.GetStream(session.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Theory]
    [InlineData(RemoteFailure.Busy)]
    [InlineData(RemoteFailure.StreamLimitReached)]
    public async Task A_refusal_on_this_side_or_for_the_limit_is_no_failure_of_the_source(RemoteFailure failure)
    {
        var session = Session(PlaybackStreamKind.Direct);
        StreamsFailWith(failure);
        await _controller.GetStream(session.Id, CancellationToken.None);
        _fixture.Client.Open = (_, _) => Task.FromResult(FakePlaybackClient.Bytes());

        var result = await _controller.GetStream(session.Id, CancellationToken.None);

        Assert.IsType<EmptyResult>(result);
        Assert.False(session.HasFailed);
    }

    [Fact]
    public async Task A_missing_subtitle_is_no_failure_of_the_source()
    {
        var session = Session(PlaybackStreamKind.Hls);
        var url = session.MapRootedUrl("/Videos/remote-item/ms/Subtitles/0/0/Stream.vtt", PlaybackValues.LocalToken)!;
        var parts = url.Split('?')[0].Split('/');
        StreamsFailWith(RemoteFailure.ServerError);

        var result = await _controller.GetFile(session.Id, int.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture), parts[^1], CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.False(session.HasFailed);
    }

    // A remote body whose connection breaks after the headers.
    private sealed class DroppingStream : System.IO.MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new System.IO.IOException("The remote went away.");
    }

    private sealed class CompletionFeature : HttpResponseFeature
    {
        private Func<object, Task>? _completed;
        private object? _state;

        public override void OnCompleted(Func<object, Task> callback, object state)
        {
            _completed = callback;
            _state = state;
        }

        public Task CompleteAsync() => _completed?.Invoke(_state!) ?? Task.CompletedTask;
    }
}
