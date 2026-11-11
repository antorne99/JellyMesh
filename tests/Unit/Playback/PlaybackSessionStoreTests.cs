using System;
using Jellyfin.Plugin.JellyMesh.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class PlaybackSessionStoreTests
{
    private static readonly Guid _owner = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid _stub = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void A_local_play_session_outlives_a_short_idle_time_until_the_minimum()
    {
        var clock = new ManualClock();
        var sessions = new PlaybackSessionStore(clock);
        sessions.RecordLocal(_owner, "local-play", _stub);

        clock.Advance(PlaybackTimeouts.LocalIdleMinimum - TimeSpan.FromSeconds(1));
        sessions.ForgetIdleLocal(TimeSpan.FromSeconds(30));
        Assert.True(sessions.IsLocalOfAnother(_other, "local-play"));

        clock.Advance(TimeSpan.FromSeconds(1));
        sessions.ForgetIdleLocal(TimeSpan.FromSeconds(30));
        Assert.False(sessions.IsLocalOfAnother(_other, "local-play"));
    }

    [Fact]
    public void A_local_play_session_follows_an_idle_time_above_the_minimum()
    {
        var clock = new ManualClock();
        var sessions = new PlaybackSessionStore(clock);
        sessions.RecordLocal(_owner, "local-play", _stub);
        var idle = PlaybackTimeouts.LocalIdleMinimum + TimeSpan.FromSeconds(60);

        clock.Advance(idle - TimeSpan.FromSeconds(1));
        sessions.ForgetIdleLocal(idle);
        Assert.True(sessions.IsLocalOfAnother(_other, "local-play"));

        clock.Advance(TimeSpan.FromSeconds(1));
        sessions.ForgetIdleLocal(idle);
        Assert.False(sessions.IsLocalOfAnother(_other, "local-play"));
    }

    [Fact]
    public void The_local_idle_minimum_outlasts_a_stock_hls_job()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), PlaybackTimeouts.LocalIdleMinimum);
    }
}
