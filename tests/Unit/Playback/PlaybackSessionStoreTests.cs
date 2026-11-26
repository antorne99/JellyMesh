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
    public void A_session_taken_out_of_the_store_refuses_a_request_of_whoever_still_holds_it()
    {
        var clock = new ManualClock();
        var sessions = new PlaybackSessionStore(clock);
        var session = PlaybackValues.Session(clock: clock);
        sessions.Add(session);

        sessions.Remove(session.Id);

        Assert.True(session.IsClosed);
        Assert.Null(session.TryBeginRequest(clock));
    }

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

    [Fact]
    public void The_local_play_sessions_of_others_are_those_their_owners_did_not_end()
    {
        var sessions = new PlaybackSessionStore(new ManualClock());
        sessions.RecordLocal(_owner, "local-play", _stub, "owner-device");
        sessions.RecordLocal(_owner, "ended", _stub);

        sessions.EndLocal(_owner, "ended");

        Assert.Equal(["local-play"], sessions.LocalSessionsOfOthers(_other));
        Assert.Empty(sessions.LocalSessionsOfOthers(_owner));
    }

    // Row 561: Finamp names the play session itself, and reports the stop with that name.
    [Fact]
    public void A_stop_report_finds_the_sessions_its_user_named_and_leaves_the_others()
    {
        var sessions = new PlaybackSessionStore();
        var named = PlaybackValues.Session();
        var next = PlaybackValues.Session();
        var ofAnother = new PlaybackSession(PlaybackValues.Identity() with { UserId = _other }, PlaybackValues.Source(), null, PlaybackStreamKind.Hls, "/m", DateTimeOffset.UtcNow);
        named.NameByClient("finamp-play");
        next.NameByClient("finamp-next");
        ofAnother.NameByClient("finamp-play");
        sessions.Add(named);
        sessions.Add(next);
        sessions.Add(ofAnother);
        var owner = named.Identity.UserId;

        Assert.Same(named, Assert.Single(sessions.FindReported(owner, "finamp-play")));
        Assert.Same(named, Assert.Single(sessions.FindReported(owner, named.Id)));
        Assert.Same(ofAnother, Assert.Single(sessions.FindReported(_other, "finamp-play")));
        Assert.Empty(sessions.FindReported(_other, named.Id));
        Assert.Empty(sessions.FindReported(owner, null));
    }

    [Fact]
    public void A_session_keeps_the_first_name_its_client_chose()
    {
        var session = PlaybackValues.Session();

        session.NameByClient("first");
        session.NameByClient("second");

        Assert.Equal("first", session.ClientPlaySessionId);
    }
}
