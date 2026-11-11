using System;
using Jellyfin.Plugin.JellyMesh.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public class PlaybackSessionStoreTests
{
    private static readonly Guid _owner = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid _stub = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static readonly PlaybackTimes _times = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), TimeSpan.FromHours(1));

    [Fact]
    public void A_local_play_session_stays_guarded_through_a_pause_longer_than_the_idle_time()
    {
        var clock = new ManualClock();
        var sessions = new PlaybackSessionStore(clock);
        sessions.RecordLocal(_owner, "local-play", _stub);

        clock.Advance(TimeSpan.FromMinutes(30));
        sessions.ForgetUnusedLocal(_times);

        Assert.True(sessions.IsLocalOfAnother(_other, "local-play"));
    }

    [Fact]
    public void A_local_play_session_leaves_after_the_session_retention()
    {
        var clock = new ManualClock();
        var sessions = new PlaybackSessionStore(clock);
        sessions.RecordLocal(_owner, "local-play", _stub);

        clock.Advance(_times.SessionRetention - TimeSpan.FromSeconds(1));
        sessions.ForgetUnusedLocal(_times);
        Assert.True(sessions.IsLocalOfAnother(_other, "local-play"));

        clock.Advance(TimeSpan.FromSeconds(1));
        sessions.ForgetUnusedLocal(_times);
        Assert.False(sessions.IsLocalOfAnother(_other, "local-play"));
    }
}
