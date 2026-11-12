using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task A_sweep_that_fails_is_logged_and_the_next_one_still_runs()
    {
        var clock = new HandClock();
        var store = new FailingOnceStore(_fixture.Store);
        var logger = new CapturingLogger<PlaybackSweeper>();
        using var sweeper = new PlaybackSweeper(_fixture.Coordinator, _sessionManager.Object, _fixture.Sessions, store, clock, logger);
        using var stop = new CancellationTokenSource();
        await sweeper.StartAsync(stop.Token);
        for (var waited = 0; waited < 200 && store.Reads < 2; waited++)
        {
            clock.Tick();
            await Task.Delay(10);
        }

        await stop.CancelAsync();
        await sweeper.StopAsync(CancellationToken.None);

        Assert.True(store.Reads >= 2, store.Reads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(LogLevel.Error, logger.Levels);
        Assert.DoesNotContain(logger.Lines, line => line.Contains(FailingOnceStore.Secret, StringComparison.Ordinal));
    }

    // A clock whose timers fire only when the test ticks it.
    private sealed class HandClock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
            {
                _timers.Add((callback, state));
            }

            return new HandTimer();
        }

        public void Tick()
        {
            List<(TimerCallback Callback, object? State)> timers;
            lock (_timers)
            {
                timers = _timers.ToList();
            }

            foreach (var (callback, state) in timers)
            {
                callback(state);
            }
        }

        private sealed class HandTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // Throws on its first read, with a remote address and a token in the message.
    private sealed class FailingOnceStore(IPluginConfigurationStore inner) : IPluginConfigurationStore
    {
        public const string Secret = "http://10.9.8.7:8096?api_key=sweeper-secret";

        private int _reads;

        public event EventHandler? SettingsChanged
        {
            add => inner.SettingsChanged += value;
            remove => inner.SettingsChanged -= value;
        }

        public int Reads => Volatile.Read(ref _reads);

        public T Read<T>(Func<PluginConfiguration, T> read)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                throw new InvalidOperationException(Secret);
            }

            return inner.Read(read);
        }

        public void Update(Action<PluginConfiguration> update) => inner.Update(update);
    }
}
