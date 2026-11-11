using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class PlaybackSweeperTests
{
    private static readonly Guid _stub = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly CoordinatorFixture _fixture = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly User _owner = new("owner", "auth", "reset") { Id = Guid.NewGuid() };

    // The service subscribes on a thread of its own, so the event is raised until the record shows it was heard.
    private async Task StopReportedAsync(User user, Func<bool> heard)
    {
        using var sweeper = new PlaybackSweeper(_fixture.Coordinator, _sessionManager.Object, _fixture.Sessions, _fixture.Store, TimeProvider.System, NullLogger<PlaybackSweeper>.Instance);
        using var stop = new CancellationTokenSource();
        await sweeper.StartAsync(stop.Token);
        for (var waited = 0; waited < 100 && !heard(); waited++)
        {
            _sessionManager.Raise(manager => manager.PlaybackStopped += null, new PlaybackStopEventArgs { PlaySessionId = "local-play", Users = [user] });
            await Task.Delay(10);
        }

        await stop.CancelAsync();
        await sweeper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_stop_its_user_reports_ends_a_local_play_session()
    {
        _fixture.Sessions.RecordLocal(_owner.Id, "local-play", _stub);

        await StopReportedAsync(_owner, () => !_fixture.Sessions.IsLocalOfAnother(Guid.NewGuid(), "local-play"));

        Assert.False(_fixture.Sessions.IsLocalOfAnother(Guid.NewGuid(), "local-play"));
    }

    [Fact]
    public async Task A_stop_of_another_user_leaves_a_local_play_session()
    {
        _fixture.Sessions.RecordLocal(_owner.Id, "local-play", _stub);

        await StopReportedAsync(new User("other", "auth", "reset") { Id = Guid.NewGuid() }, () => false);

        Assert.True(_fixture.Sessions.IsLocalOfAnother(Guid.NewGuid(), "local-play"));
    }
}
