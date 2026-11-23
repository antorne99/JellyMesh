using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

/// <summary>The streams of one device of a peer count as one (row 511).</summary>
public class StreamSlotsDeviceTests
{
    private const string Phone = "phone";
    private const string Tablet = "tablet";

    private static readonly Guid _peer = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid _track = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid _next = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly ManualClock _clock = new();
    private readonly InMemoryConfigurationStore _store = new();
    private readonly StreamSlots _slots;

    public StreamSlotsDeviceTests()
    {
        _slots = new StreamSlots(_clock, _store);
    }

    private bool Opens(string playSession, string? device, int limit = 1, string? replaces = null, Guid peer = default)
    {
        var id = peer.Equals(Guid.Empty) ? _peer : peer;
        if (_slots.TryReserve(id, limit, replaces, device) is not { } reservation)
        {
            return false;
        }

        _slots.Confirm(id, reservation, playSession, _track);
        return true;
    }

    private StreamRequest? Reads(string? playSession, string? device, int limit = 1, Guid? item = null, Action? cut = null)
        => _slots.TryEnter(_peer, limit, playSession, item ?? _track, cut, device);

    private bool ReadsOnce(string? playSession, string? device, int limit = 1, Guid? item = null)
    {
        using var request = Reads(playSession, device, limit, item);
        return request is not null;
    }

    private void Abandons(string? playSession, string? device, int limit = 1)
    {
        using var request = Reads(playSession, device, limit);
        Assert.NotNull(request);
        request.Abandon();
    }

    private void Close()
    {
        foreach (var playSession in _slots.TakeClosing())
        {
            _slots.Closed(playSession);
        }
    }

    [Fact]
    public void The_next_track_a_device_prepares_takes_no_other_slot()
    {
        Assert.True(Opens("playing", Phone));
        Assert.True(Opens("next", Phone));

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_reservation_already_counts_for_its_device()
    {
        Assert.NotNull(_slots.TryReserve(_peer, 1, null, Phone));
        Assert.NotNull(_slots.TryReserve(_peer, 1, null, Phone));

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Another_device_takes_a_slot_of_its_own()
    {
        Assert.True(Opens("one", Phone));

        Assert.False(Opens("two", Tablet));
        Assert.True(Opens("two", Tablet, 2));
        Assert.Equal(2, _slots.Count(_peer));
    }

    [Fact]
    public void A_stream_with_no_device_takes_a_slot_of_its_own()
    {
        Assert.True(Opens("track", Phone));

        Assert.False(Opens("film", null));
        Assert.False(ReadsOnce(null, null));
        Assert.True(Opens("film", null, 2));
        Assert.True(Opens("next", Phone, 2));
        Assert.False(Opens("another film", null, 2));
        Assert.Equal(2, _slots.Count(_peer));
    }

    [Fact]
    public void A_device_of_one_peer_is_not_that_of_another()
    {
        Assert.True(Opens("one", Phone));
        Assert.True(Opens("two", Phone, peer: _other));
        Assert.False(Opens("three", Tablet, peer: _other));

        Assert.Equal(1, _slots.Count(_peer));
        Assert.Equal(1, _slots.Count(_other));
    }

    [Fact]
    public void Concurrent_reservations_of_several_devices_never_pass_the_limit()
    {
        Parallel.For(0, 200, number => _slots.TryReserve(_peer, 3, null, "device " + (number % 10)));

        Assert.Equal(3, _slots.Count(_peer));
    }

    [Fact]
    public void A_device_holds_its_slot_until_its_last_stream_goes()
    {
        Opens("playing", Phone);
        Opens("next", Phone);

        _slots.Release(_peer, "playing");
        Assert.False(Opens("other", Tablet));

        _slots.Release(_peer, "next");
        Assert.True(Opens("other", Tablet));
    }

    [Fact]
    public void The_streams_of_a_device_expire_each_on_its_own()
    {
        Opens("playing", Phone);
        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));
        Opens("next", Phone);
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(["playing"], _slots.TakeClosing());
        _slots.Closed("playing");
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_replacement_hands_over_the_slot_of_its_device()
    {
        Opens("one", Phone);

        Assert.True(Opens("two", Phone, replaces: "one"));
        Assert.Equal(1, _slots.Count(_peer));
        Assert.True(Opens("three", Tablet, replaces: "two"));
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_paused_track_that_lost_its_slot_comes_back_into_the_one_of_its_device()
    {
        Opens("playing", Phone);
        _clock.Advance(StreamSlots.IdleTimeout);
        Close();
        Opens("next", Phone);

        Assert.True(ReadsOnce("playing", Phone));
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_read_with_no_play_session_uses_the_slot_of_its_device()
    {
        Opens("playing", Phone);

        Assert.True(ReadsOnce(null, Phone));
        Assert.False(ReadsOnce(null, Tablet));
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_read_with_no_play_session_opens_the_slot_of_its_device_and_keeps_it_for_the_idle_time()
    {
        Assert.True(ReadsOnce(null, Phone));

        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, _slots.Count(_peer));
        Assert.False(Opens("other", Tablet));
        Assert.True(Opens("next", Phone));
        _slots.Release(_peer, "next");
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void Each_read_with_no_play_session_restarts_the_idle_time()
    {
        Assert.True(ReadsOnce(null, Phone));
        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.True(ReadsOnce(null, Phone, item: _next));
        Assert.True(ReadsOnce(null, Phone));
        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_read_with_no_play_session_whose_connection_was_lost_keeps_its_slot_for_the_grace_only()
    {
        Abandons(null, Phone);

        _clock.Advance(StreamSlots.AbandonGrace - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, _slots.Count(_peer));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, _slots.Count(_peer));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_read_with_no_play_session_never_has_a_transcode_to_close(bool closeAtExpiry)
    {
        _store.Configuration.CloseTranscodeAtSlotExpiry = closeAtExpiry;

        Assert.True(ReadsOnce(null, Phone));
        _clock.Advance(StreamSlots.IdleTimeout);
        Assert.Empty(_slots.TakeClosing());

        Abandons(null, Phone);
        _clock.Advance(StreamSlots.AbandonGrace);
        Assert.Empty(_slots.TakeClosing());

        using var request = Reads(null, Phone);
        _slots.Revoke(_peer, _ => false);
        Assert.Empty(_slots.TakeClosing());
        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void Revoking_cuts_the_reads_of_a_device_on_what_was_lost_and_nothing_else()
    {
        var cut = new List<string>();
        using var kept = Reads(null, Phone, item: _track, cut: () => cut.Add("kept"));
        using var lost = Reads(null, Phone, item: _next, cut: () => cut.Add("lost"));

        _slots.Revoke(_peer, item => item.Equals(_track));

        Assert.Equal(["lost"], cut);
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Coming_down_to_a_lower_limit_ends_whole_devices_and_keeps_the_oldest()
    {
        Opens("phone one", Phone, 3);
        Opens("film", null, 3);
        Opens("tablet one", Tablet, 3);
        Opens("phone two", Phone, 3);
        Opens("tablet two", Tablet, 3);

        _slots.CutToLimit(_peer, 2);

        Assert.Equal(["tablet one", "tablet two"], _slots.TakeClosing().Order(StringComparer.Ordinal));
        Assert.Equal(3, _slots.Count(_peer));
        _slots.Closed("tablet one");
        Assert.Equal(3, _slots.Count(_peer));
        _slots.Closed("tablet two");
        Assert.Equal(2, _slots.Count(_peer));
    }

    [Fact]
    public void Coming_down_to_a_lower_limit_cuts_every_request_of_the_devices_that_go()
    {
        var cut = new List<string>();
        Opens("phone", Phone, 2);
        Opens("tablet", Tablet, 2);
        using var first = Reads("phone", Phone, 2, cut: () => cut.Add("phone"));
        using var second = Reads("tablet", Tablet, 2, cut: () => cut.Add("tablet"));
        using var third = Reads(null, Tablet, 2, cut: () => cut.Add("tablet file"));

        _slots.CutToLimit(_peer, 1);

        Assert.Equal(["tablet", "tablet file"], cut.Order(StringComparer.Ordinal));
        Assert.Equal(["tablet"], _slots.TakeClosing());
    }

    [Fact]
    public void Coming_down_to_a_lower_limit_ends_a_device_that_only_reserved_before_one_that_plays()
    {
        _slots.TryReserve(_peer, 2, null, Phone);
        Opens("tablet", Tablet, 2);
        _slots.TryReserve(_peer, 2, null, Tablet);

        _slots.CutToLimit(_peer, 1);

        Assert.Equal(1, _slots.Count(_peer));
        Assert.Empty(_slots.TakeClosing());
        Assert.True(Opens("next", Tablet));
    }

    [Fact]
    public void A_device_that_was_cut_does_not_come_back_through_its_stream_being_closed()
    {
        Opens("phone", Phone, 2);
        Opens("tablet", Tablet, 2);
        _slots.CutToLimit(_peer, 1);

        Assert.False(Opens("tablet again", Tablet));
        Assert.False(ReadsOnce(null, Tablet));
        Assert.False(ReadsOnce("tablet", Tablet));

        Close();
        Assert.False(Opens("tablet again", Tablet));
        Assert.True(Opens("phone again", Phone));
    }

    [Fact]
    public void A_device_whose_only_stream_is_being_closed_waits_for_the_close()
    {
        Opens("one", Phone);
        Abandons("one", Phone);
        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.False(Opens("two", Phone));
        Close();
        Assert.True(Opens("two", Phone));
    }

    [Fact]
    public void A_stream_being_closed_comes_back_into_the_slot_its_device_still_holds()
    {
        Opens("one", Phone);
        Abandons("one", Phone);
        Opens("two", Phone);
        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.True(ReadsOnce("one", Phone));
        Assert.Equal(1, _slots.Count(_peer));
        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void A_stream_being_closed_comes_back_counting_devices_and_not_streams()
    {
        Opens("phone one", Phone, 2);
        Opens("phone two", Phone, 2);
        Opens("tablet", Tablet, 2);
        Abandons("tablet", Tablet, 2);
        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.True(ReadsOnce("tablet", Tablet, 2));
    }

    // A play session opened on a track must not carry a stream of anything else past the limit.
    [Fact]
    public void A_stream_that_reads_with_no_device_leaves_the_slot_of_its_device()
    {
        Opens("track", Phone);
        Opens("next", Phone);

        Assert.False(ReadsOnce("next", null));
        Assert.Equal(1, _slots.Count(_peer));

        Assert.True(ReadsOnce("next", null, 2));
        Assert.Equal(2, _slots.Count(_peer));
        Assert.True(ReadsOnce("next", Phone, 2));
        Assert.Equal(2, _slots.Count(_peer));
    }

    [Fact]
    public void A_stream_alone_on_its_device_reads_with_no_device_on_the_same_slot()
    {
        Opens("track", Phone);

        Assert.True(ReadsOnce("track", null));
        Assert.Equal(1, _slots.Count(_peer));
        Assert.False(Opens("next", Phone));
    }

    [Fact]
    public void A_stream_being_closed_comes_back_with_no_device_only_if_it_has_room_of_its_own()
    {
        Opens("one", Phone);
        Abandons("one", Phone);
        Opens("two", Phone);
        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.False(ReadsOnce("one", null));
        Assert.True(ReadsOnce("one", null, 2));
        Assert.Equal(2, _slots.Count(_peer));
    }

    // Stock serves the job of a play session whatever item the request names.
    [Fact]
    public void A_play_session_of_another_item_never_shares_the_slot_of_a_device()
    {
        Opens("track", Phone);
        Opens("film", null, 2);
        _slots.Release(_peer, "film");

        Assert.False(ReadsOnce("film", Phone, item: _next));
        Assert.True(ReadsOnce("film", Phone, 2, _next));
        Assert.Equal(2, _slots.Count(_peer));
    }

    [Fact]
    public void A_play_session_that_once_read_another_item_never_shares_the_slot_of_a_device_again()
    {
        Opens("track", Phone);
        Opens("next", Phone);
        Assert.True(ReadsOnce("next", null, 2, _next));
        _slots.Release(_peer, "next");

        Assert.False(ReadsOnce("next", Phone));
        Assert.True(ReadsOnce("next", Phone, 2));
        Assert.Equal(2, _slots.Count(_peer));
    }

    [Fact]
    public void A_play_session_the_peer_did_not_open_never_shares_the_slot_of_a_device()
    {
        Opens("track", Phone);

        Assert.False(ReadsOnce("unknown", Phone));
    }

    [Fact]
    public void A_stream_with_no_device_never_joins_one()
    {
        Opens("film", null);
        Assert.True(ReadsOnce("film", Phone));

        Assert.False(Opens("next", Phone));
        Assert.Equal(1, _slots.Count(_peer));
    }
}
