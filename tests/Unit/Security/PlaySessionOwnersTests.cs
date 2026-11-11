using System;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

public class PlaySessionOwnersTests
{
    private static readonly Guid _owner = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly PlaySessionOwners _owners = new();

    private void Flood(Guid owner, int count)
    {
        for (var index = 0; index < count; index++)
        {
            _owners.Add(owner, "flood" + index);
        }
    }

    [Fact]
    public void An_owner_keeps_no_more_than_the_limit_and_the_oldest_goes_first()
    {
        _owners.Add(_owner, "oldest");

        Flood(_owner, PlaySessionOwners.MaxPerOwner);

        Assert.False(_owners.Owns(_owner, "oldest"));
        Assert.True(_owners.Owns(_owner, "flood0"));
        Assert.True(_owners.Owns(_owner, "flood" + (PlaySessionOwners.MaxPerOwner - 1)));
    }

    [Fact]
    public void A_session_in_use_stays_through_a_flood()
    {
        _owners.Add(_owner, "in-use");

        for (var index = 0; index < PlaySessionOwners.MaxPerOwner; index++)
        {
            Assert.True(_owners.Owns(_owner, "in-use"));
            _owners.Add(_owner, "flood" + index);
        }

        Assert.True(_owners.Owns(_owner, "in-use"));
    }

    [Fact]
    public void A_flood_only_pushes_out_what_the_flooder_owns()
    {
        _owners.Add(_other, "theirs");

        Flood(_owner, PlaySessionOwners.MaxPerOwner * 2);

        Assert.True(_owners.Owns(_other, "theirs"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void Nothing_else_is_owned_by_anyone(string? playSession)
    {
        _owners.Add(_owner, "mine");

        Assert.False(_owners.Owns(_owner, playSession));
        Assert.False(_owners.BelongsToAnother(_other, playSession));
    }

    [Fact]
    public void The_item_of_a_session_is_known_to_anyone_who_names_it()
    {
        var item = Guid.NewGuid();
        _owners.Add(_owner, "mine", item);
        _owners.Add(_owner, "bare");

        Assert.Equal(item, _owners.ItemOf("MINE"));
        Assert.Null(_owners.ItemOf("bare"));
        Assert.Null(_owners.ItemOf("unknown"));
        Assert.Null(_owners.ItemOf(null));
    }

    [Fact]
    public void A_session_belongs_to_another_only_for_someone_else()
    {
        _owners.Add(_owner, "mine");

        Assert.True(_owners.BelongsToAnother(_other, "mine"));
        Assert.True(_owners.BelongsToAnother(_other, "MINE"));
        Assert.False(_owners.BelongsToAnother(_owner, "mine"));
    }

    [Fact]
    public void A_session_leaves_when_its_owner_ends_it_and_only_then()
    {
        _owners.Add(_owner, "mine", Guid.NewGuid());

        Assert.False(_owners.Remove(_other, "mine"));
        Assert.True(_owners.BelongsToAnother(_other, "mine"));

        Assert.True(_owners.Remove(_owner, "MINE"));
        Assert.False(_owners.BelongsToAnother(_other, "mine"));
        Assert.Null(_owners.ItemOf("mine"));
    }

    [Fact]
    public void A_session_leaves_after_going_unused_for_the_idle_time()
    {
        var clock = new ManualClock();
        var owners = PlaySessionOwners.WithClock(clock);
        var idle = TimeSpan.FromSeconds(90);
        owners.Add(_owner, "quiet");
        owners.Add(_owner, "used");

        clock.Advance(idle - TimeSpan.FromSeconds(1));
        Assert.True(owners.Owns(_owner, "used"));
        owners.ForgetIdle(idle);
        Assert.True(owners.BelongsToAnother(_other, "quiet"));

        clock.Advance(TimeSpan.FromSeconds(1));
        owners.ForgetIdle(idle);
        Assert.False(owners.BelongsToAnother(_other, "quiet"));
        Assert.True(owners.BelongsToAnother(_other, "used"));
    }

    [Fact]
    public void The_sessions_of_others_are_every_session_someone_else_opened()
    {
        Assert.Empty(_owners.SessionsOfOthers(_other));

        _owners.Add(_owner, "mine", null, ["owner-device"]);
        _owners.Add(_owner, "without-devices");
        _owners.Add(_other, "theirs");

        Assert.Equal(["mine", "without-devices"], _owners.SessionsOfOthers(_other).Order());
        Assert.Equal(["mine", "theirs", "without-devices"], _owners.SessionsOfOthers(Guid.Empty).Order());
        Assert.Equal(["theirs"], _owners.SessionsOfOthers(_owner));
    }

    [Fact]
    public void A_session_stops_being_of_others_once_it_leaves()
    {
        var clock = new ManualClock();
        var owners = PlaySessionOwners.WithClock(clock);
        owners.Add(_owner, "ended");
        owners.Add(_other, "quiet");

        Assert.True(owners.Remove(_owner, "ended"));
        Assert.Empty(owners.SessionsOfOthers(_other));
        Assert.Equal(["quiet"], owners.SessionsOfOthers(_owner));

        clock.Advance(TimeSpan.FromSeconds(90));
        owners.ForgetIdle(TimeSpan.FromSeconds(90));
        Assert.Empty(owners.SessionsOfOthers(_owner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void No_device_names_no_device_of_another(string? device)
    {
        _owners.Add(_owner, "mine", null, ["owner-device"]);
        _owners.Add(_owner, "without-devices");

        Assert.False(_owners.DeviceOfAnother(_other, device));
        Assert.False(_owners.DeviceOfAnother(_owner, device));
    }
}
