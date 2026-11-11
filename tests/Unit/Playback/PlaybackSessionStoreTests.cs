using System;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Security;
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

    [Fact]
    public void An_unused_local_play_session_leaves_once_its_owner_opens_the_limit_more()
    {
        var sessions = new PlaybackSessionStore(new ManualClock());
        sessions.RecordLocal(_owner, "local-play", _stub);
        sessions.RecordLocal(_other, "of-another", _stub);

        for (var index = 0; index < PlaySessionOwners.MaxPerOwner; index++)
        {
            sessions.RecordLocal(_owner, "flood" + index, _stub);
        }

        Assert.False(sessions.IsLocalOfAnother(_other, "local-play"));
        Assert.True(sessions.IsLocalOfAnother(_other, "flood0"));
        Assert.True(sessions.IsLocalOfAnother(_owner, "of-another"));
    }

    [Fact]
    public void A_local_play_session_its_owner_keeps_using_stays_however_many_they_open()
    {
        var sessions = new PlaybackSessionStore(new ManualClock());
        sessions.RecordLocal(_owner, "local-play", _stub);

        for (var index = 0; index < PlaySessionOwners.MaxPerOwner * 2; index++)
        {
            if (index % 25 == 0)
            {
                sessions.UseLocal(_owner, "local-play");
            }

            sessions.RecordLocal(_owner, "flood" + index, _stub);
        }

        Assert.True(sessions.IsLocalOfAnother(_other, "local-play"));
    }

    [Fact]
    public void The_device_of_a_local_play_session_belongs_to_its_owner_until_they_end_it()
    {
        var sessions = new PlaybackSessionStore(new ManualClock());
        sessions.RecordLocal(_owner, "local-play", _stub, "owner-device");

        Assert.True(sessions.IsLocalDeviceOfAnother(_other, "OWNER-DEVICE"));
        Assert.False(sessions.IsLocalDeviceOfAnother(_owner, "owner-device"));
        Assert.False(sessions.IsLocalDeviceOfAnother(_other, "another-device"));
        Assert.False(sessions.IsLocalDeviceOfAnother(_other, null));

        sessions.EndLocal(_owner, "local-play");

        Assert.False(sessions.IsLocalDeviceOfAnother(_other, "owner-device"));
    }
}
