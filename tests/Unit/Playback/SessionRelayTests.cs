using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

/// <summary>The relay shared by the session routes and the track routes.</summary>
public class SessionRelayTests
{
    private readonly CoordinatorFixture _fixture = new();

    [Fact]
    public async Task A_closed_track_session_sends_nothing_answers_not_found_and_does_not_fail()
    {
        var source = PlaybackValues.Source("remote-item");
        _fixture.Registry.AddJellyMesh(source.ServerId);
        var path = PlaybackUrls.WithoutToken(PlaybackValues.AudioMaster);
        var session = new PlaybackSession(PlaybackValues.Identity(), source, PlaybackValues.RemotePlaySession, PlaybackStreamKind.Hls, path, _fixture.Clock.GetUtcNow()) { RemoteDevice = "device" };
        var relay = new SessionRelay(_fixture.Coordinator, new PlaybackRefusal(Mock.Of<ISessionManager>(), NullLogger<PlaybackRefusal>.Instance), _fixture.Clock, NullLogger.Instance);
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        session.Close();

        var playlist = await relay.PlaylistAsync(http, session, path, session.UpstreamFolder(), string.Empty, PlaybackValues.LocalToken, SessionRelay.PlaylistType, CancellationToken.None);
        var stream = await relay.StreamAsync(http, session, "/Audio/remote-item/hls1/main/0.mp4", CancellationToken.None);

        Assert.IsType<NotFoundResult>(playlist);
        Assert.IsType<NotFoundResult>(stream);
        Assert.Empty(_fixture.Client.Devices);
        Assert.False(session.HasFailed);
    }
}
