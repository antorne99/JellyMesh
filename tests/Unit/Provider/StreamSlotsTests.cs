using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class StreamSlotsTests
{
    private static readonly Guid _peer = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly ManualClock _clock = new();
    private readonly InMemoryConfigurationStore _store = new();
    private readonly StreamSlots _slots;

    public StreamSlotsTests()
    {
        _slots = new StreamSlots(_clock, _store);
    }

    private void Hold(string playSession, int limit = 1)
    {
        var reservation = _slots.TryReserve(_peer, limit, null);
        Assert.NotNull(reservation);
        _slots.Confirm(_peer, reservation, playSession);
    }

    // What the closer does once the transcodes are gone.
    private void Close()
    {
        foreach (var playSession in _slots.TakeClosing())
        {
            _slots.Closed(playSession);
        }
    }

    [Fact]
    public void An_abandoned_request_frees_the_slot_after_the_grace_only()
    {
        Hold("one");
        var request = _slots.TryEnter(_peer, 1, "one")!;

        request.Abandon();
        request.Dispose();

        _clock.Advance(StreamSlots.AbandonGrace - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, _slots.Count(_peer));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Close();
        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void The_grace_is_much_shorter_than_the_backstop_and_longer_than_a_seek()
    {
        Assert.True(StreamSlots.AbandonGrace < StreamSlots.IdleTimeout / 10);
        Assert.True(StreamSlots.AbandonGrace >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_new_request_within_the_grace_cancels_it()
    {
        Hold("one");
        var request = _slots.TryEnter(_peer, 1, "one")!;
        request.Abandon();
        request.Dispose();

        _slots.TryEnter(_peer, 1, "one")!.Dispose();
        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_renewal_within_the_grace_cancels_it()
    {
        Hold("one");
        var request = _slots.TryEnter(_peer, 1, "one")!;
        request.Abandon();
        request.Dispose();

        _slots.Renew(_peer, "one");
        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void The_last_request_to_end_decides()
    {
        Hold("one");
        var first = _slots.TryEnter(_peer, 1, "one")!;
        var second = _slots.TryEnter(_peer, 1, "one")!;

        first.Abandon();
        first.Dispose();
        second.Dispose();
        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void An_abandoned_request_is_counted_once()
    {
        Hold("one");
        var request = _slots.TryEnter(_peer, 1, "one")!;
        using var other = _slots.TryEnter(_peer, 1, "one");

        request.Abandon();
        request.Dispose();
        request.Dispose();
        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Everything_of_a_peer_can_be_freed_at_once_and_only_of_that_peer()
    {
        Hold("one", 3);
        Hold("two", 3);
        using var running = _slots.TryEnter(_peer, 3, "two");
        var other = _slots.TryReserve(_other, 1, null);

        _slots.ReleaseAll(_peer);

        Assert.Equal(0, _slots.Count(_peer));
        Assert.Equal(1, _slots.Count(_other));
        Assert.NotNull(other);
    }

    [Fact]
    public void A_request_running_when_its_slot_was_freed_ends_without_harm()
    {
        Hold("one");
        var running = _slots.TryEnter(_peer, 1, "one")!;

        _slots.ReleaseAll(_peer);
        Hold("two");
        running.Abandon();
        running.Dispose();

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Freeing_the_slots_leaves_the_play_sessions_of_the_peer_its_own()
    {
        Hold("one");

        _slots.ReleaseAll(_peer);

        Assert.True(_slots.Owns(_peer, "one"));
    }

    [Fact]
    public void A_play_session_stays_owned_after_its_slot_expired_or_was_released()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);
        Close();
        Assert.Equal(0, _slots.Count(_peer));
        Assert.True(_slots.Owns(_peer, "one"));

        Hold("two");
        _slots.Release(_peer, "two");
        Assert.True(_slots.Owns(_peer, "two"));
    }

    [Fact]
    public void A_reservation_is_not_a_play_session_anyone_owns()
    {
        var reservation = _slots.TryReserve(_peer, 1, null)!;

        Assert.False(_slots.Owns(_peer, reservation));
    }

    [Fact]
    public void A_renewal_keeps_a_long_stream_among_those_remembered()
    {
        Hold("long");
        for (var index = 0; index < PlaySessionOwners.MaxPerOwner; index++)
        {
            _slots.Renew(_peer, "long");
            var reservation = _slots.TryReserve(_peer, int.MaxValue, null)!;
            _slots.Confirm(_peer, reservation, "other" + index);
            _slots.Release(_peer, "other" + index);
        }

        Assert.True(_slots.Owns(_peer, "long"));
    }

    private static readonly Guid _kept = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _lost = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private void Revoke() => _slots.Revoke(_peer, item => item == _kept);

    private void Paused(string playSession, Guid item)
    {
        var reservation = _slots.TryReserve(_peer, int.MaxValue, null)!;
        _slots.Confirm(_peer, reservation, playSession, item);
    }

    [Fact]
    public void Revoking_cuts_what_is_running_on_what_was_lost_and_nothing_else()
    {
        var cut = new List<string>();
        using var kept = _slots.TryEnter(_peer, 3, "kept", _kept, () => cut.Add("kept"));
        using var lost = _slots.TryEnter(_peer, 3, "lost", _lost, () => cut.Add("lost"));

        Revoke();

        Assert.Equal(["lost"], cut);
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Revoking_cuts_a_read_with_no_play_session_too()
    {
        var cut = false;
        using var read = _slots.TryEnter(_peer, 3, null, _lost, () => cut = true);

        Revoke();

        Assert.True(cut);
        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void Revoking_cuts_a_stream_that_runs_with_its_slot_already_freed()
    {
        var cut = false;
        using var running = _slots.TryEnter(_peer, 3, "lost", _lost, () => cut = true);
        _slots.ReleaseAll(_peer);

        Revoke();

        Assert.True(cut);
    }

    [Fact]
    public void Revoking_closes_the_transcodes_of_what_was_lost()
    {
        using var running = _slots.TryEnter(_peer, 3, "running", _lost);
        Paused("paused", _lost);
        Paused("kept", _kept);

        Revoke();

        Assert.Equal(["paused", "running"], _slots.TakeClosing().Order(StringComparer.Ordinal));
        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void Revoking_leaves_the_play_sessions_of_the_peer_its_own()
    {
        Paused("lost", _lost);

        Revoke();

        Assert.True(_slots.Owns(_peer, "lost"));
    }

    [Fact]
    public void A_request_that_ended_is_not_cut()
    {
        var cut = false;
        _slots.TryEnter(_peer, 3, "lost", _lost, () => cut = true)!.Dispose();

        Revoke();

        Assert.False(cut);
    }

    [Fact]
    public void Coming_down_to_a_lower_limit_ends_the_newest_and_keeps_the_oldest()
    {
        Paused("oldest", _kept);
        Paused("middle", _kept);
        Paused("newest", _kept);

        _slots.CutToLimit(_peer, 1);

        Assert.Equal(1, _slots.Count(_peer));
        Assert.Equal(["middle", "newest"], _slots.TakeClosing().Order(StringComparer.Ordinal));
        using var oldest = _slots.TryEnter(_peer, 1, "oldest", _kept);
        Assert.NotNull(oldest);
    }

    [Fact]
    public void Coming_down_to_a_lower_limit_ends_reservations_before_any_stream()
    {
        var pending = _slots.TryReserve(_peer, 3, null)!;
        Paused("oldest", _kept);
        Paused("newest", _kept);

        _slots.CutToLimit(_peer, 2);

        Assert.Equal(2, _slots.Count(_peer));
        Assert.Empty(_slots.TakeClosing());
        _slots.Confirm(_peer, pending, "late", _kept);
        Assert.Equal(2, _slots.Count(_peer));
    }

    [Fact]
    public void Coming_down_to_a_lower_limit_cuts_the_requests_of_what_goes_and_no_others()
    {
        var cut = new List<string>();
        using var oldest = _slots.TryEnter(_peer, 3, "oldest", _kept, () => cut.Add("oldest"));
        using var newest = _slots.TryEnter(_peer, 3, "newest", _kept, () => cut.Add("newest"));
        using var second = _slots.TryEnter(_peer, 3, "newest", _kept, () => cut.Add("newest again"));

        _slots.CutToLimit(_peer, 1);

        Assert.Equal(["newest", "newest again"], cut.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Coming_down_to_a_lower_limit_cuts_a_read_with_no_play_session_and_has_nothing_to_close()
    {
        Paused("oldest", _kept);
        var cut = false;
        using var read = _slots.TryEnter(_peer, 2, null, _kept, () => cut = true);

        _slots.CutToLimit(_peer, 1);

        Assert.True(cut);
        Assert.Equal(1, _slots.Count(_peer));
        Assert.Empty(_slots.TakeClosing());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void A_limit_at_or_above_what_is_held_cuts_nothing(int limit)
    {
        var cut = false;
        using var first = _slots.TryEnter(_peer, 5, "one", _kept, () => cut = true);
        using var second = _slots.TryEnter(_peer, 5, "two", _kept, () => cut = true);

        _slots.CutToLimit(_peer, limit);

        Assert.False(cut);
        Assert.Equal(2, _slots.Count(_peer));
        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void After_coming_down_a_stream_that_was_cut_is_refused()
    {
        Paused("oldest", _kept);
        Paused("newest", _kept);

        _slots.CutToLimit(_peer, 1);

        Assert.Null(_slots.TryEnter(_peer, 1, "newest", _kept));
        Assert.True(_slots.Owns(_peer, "newest"));
    }

    [Fact]
    public void Coming_down_leaves_the_others_alone()
    {
        using var other = _slots.TryEnter(_other, 3, "one", _kept);
        using var another = _slots.TryEnter(_other, 3, "two", _kept);

        _slots.CutToLimit(_peer, 1);

        Assert.Equal(2, _slots.Count(_other));
    }

    [Fact]
    public void Revoking_a_peer_leaves_the_others_alone()
    {
        var cut = false;
        using var other = _slots.TryEnter(_other, 3, "lost", _lost, () => cut = true);

        Revoke();

        Assert.False(cut);
        Assert.Equal(1, _slots.Count(_other));
    }

    [Fact]
    public void A_reservation_counts_before_the_play_session_is_known()
    {
        Assert.NotNull(_slots.TryReserve(_peer, 1, null));

        Assert.Null(_slots.TryReserve(_peer, 1, null));
    }

    [Fact]
    public void Concurrent_reservations_never_pass_the_limit()
    {
        var granted = Enumerable.Range(0, 200).AsParallel().Select(_ => _slots.TryReserve(_peer, 3, null)).Count(reservation => reservation is not null);

        Assert.Equal(3, granted);
    }

    [Fact]
    public void A_slot_is_per_peer()
    {
        Hold("one");

        Assert.NotNull(_slots.TryReserve(_other, 1, null));
    }

    [Fact]
    public void An_abandoned_reservation_frees_its_slot()
    {
        var reservation = _slots.TryReserve(_peer, 1, null);

        _slots.Release(_peer, reservation!);

        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void A_stream_of_a_known_session_passes_at_the_limit()
    {
        Hold("one");

        using var request = _slots.TryEnter(_peer, 1, "one");

        Assert.NotNull(request);
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_stream_of_another_session_is_refused_at_the_limit()
    {
        Hold("one");

        Assert.Null(_slots.TryEnter(_peer, 1, "two"));
    }

    [Fact]
    public void A_stream_without_a_session_is_refused_at_the_limit()
    {
        Hold("one");

        Assert.Null(_slots.TryEnter(_peer, 1, null));
    }

    [Fact]
    public void A_request_without_a_session_holds_its_slot_only_while_it_runs()
    {
        var request = _slots.TryEnter(_peer, 1, null);
        Assert.Equal(1, _slots.Count(_peer));

        request!.Dispose();

        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void An_idle_slot_expires_so_a_vanished_consumer_leaves_nothing()
    {
        Hold("one");

        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));
        Assert.Equal(1, _slots.Count(_peer));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Close();
        Assert.Equal(0, _slots.Count(_peer));
        Assert.NotNull(_slots.TryReserve(_peer, 1, null));
    }

    [Fact]
    public void An_idle_slot_that_expires_has_its_transcode_closed()
    {
        Hold("one");

        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Equal(["one"], _slots.TakeClosing());
        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void An_abandoned_slot_stays_counted_until_its_transcode_is_closed()
    {
        Hold("one");
        var request = _slots.TryEnter(_peer, 1, "one")!;
        request.Abandon();
        request.Dispose();
        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.Equal(["one"], _slots.TakeClosing());
        Assert.Equal(1, _slots.Count(_peer));
        Assert.Null(_slots.TryReserve(_peer, 1, null));
        Assert.Null(_slots.TryEnter(_peer, 1, "two"));

        _slots.Closed("one");

        Assert.Equal(0, _slots.Count(_peer));
        Assert.NotNull(_slots.TryReserve(_peer, 1, null));
    }

    [Fact]
    public void An_idle_slot_stays_counted_until_its_transcode_is_closed()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Null(_slots.TryReserve(_peer, 1, null));

        Close();

        Assert.NotNull(_slots.TryReserve(_peer, 1, null));
    }

    [Fact]
    public void A_slot_being_closed_is_handed_out_once()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Equal(["one"], _slots.TakeClosing());
        _clock.Advance(StreamSlots.IdleTimeout);
        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void A_stream_back_while_its_transcode_is_being_closed_keeps_its_slot()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);
        Assert.Equal(["one"], _slots.TakeClosing());

        using var back = _slots.TryEnter(_peer, 1, "one");
        _slots.Closed("one");

        Assert.NotNull(back);
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_renewal_does_not_bring_back_a_slot_being_closed()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);

        _slots.Renew(_peer, "one");

        Assert.Equal(["one"], _slots.TakeClosing());
    }

    [Fact]
    public void A_replacement_does_not_free_a_slot_being_closed()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Null(_slots.TryReserve(_peer, 1, "one"));
    }

    [Fact]
    public void A_release_leaves_a_slot_being_closed_to_its_close()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);
        Assert.Equal(["one"], _slots.TakeClosing());

        _slots.Release(_peer, "one");

        Assert.Equal(1, _slots.Count(_peer));
        _slots.Closed("one");
        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void Closing_a_slot_in_use_or_unknown_does_nothing()
    {
        Hold("one");

        _slots.Closed("one");
        _slots.Closed("unknown");

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Coming_down_to_a_lower_limit_counts_no_slot_being_closed()
    {
        Hold("old", 2);
        _clock.Advance(StreamSlots.IdleTimeout);
        Hold("new", 2);

        _slots.CutToLimit(_peer, 1);

        Assert.Equal(["old"], _slots.TakeClosing());
        _slots.Closed("old");
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Revoking_does_not_free_a_slot_being_closed()
    {
        Paused("lost", _lost);
        _clock.Advance(StreamSlots.IdleTimeout);

        Revoke();

        Assert.Equal(1, _slots.Count(_peer));
        Assert.Equal(["lost"], _slots.TakeClosing());
    }

    [Fact]
    public void With_the_setting_off_an_idle_slot_goes_and_leaves_its_transcode()
    {
        Hold("one");
        _store.Configuration.CloseTranscodeAtSlotExpiry = false;

        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Equal(0, _slots.Count(_peer));
        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void The_setting_counts_as_it_is_when_the_slot_expires()
    {
        _store.Configuration.CloseTranscodeAtSlotExpiry = false;
        Hold("one");

        _store.Configuration.CloseTranscodeAtSlotExpiry = true;
        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Equal(["one"], _slots.TakeClosing());
    }

    [Fact]
    public void With_the_setting_off_a_lost_connection_still_closes_the_transcode()
    {
        Hold("one");
        _store.Configuration.CloseTranscodeAtSlotExpiry = false;
        var request = _slots.TryEnter(_peer, 1, "one")!;
        request.Abandon();
        request.Dispose();

        _clock.Advance(StreamSlots.AbandonGrace);

        Assert.Equal(["one"], _slots.TakeClosing());
    }

    [Fact]
    public void A_request_running_at_the_idle_time_keeps_its_transcode()
    {
        Hold("one");
        using var request = _slots.TryEnter(_peer, 1, "one");

        _clock.Advance(TimeSpan.FromHours(3));

        Assert.Empty(_slots.TakeClosing());
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_request_that_outlived_its_slot_keeps_the_transcode_when_the_next_slot_expires()
    {
        using var running = _slots.TryEnter(_peer, 2, "one");
        _slots.Release(_peer, "one");
        _slots.TryEnter(_peer, 2, "one")!.Dispose();

        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Equal(0, _slots.Count(_peer));
        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void A_stream_back_after_the_idle_time_keeps_its_transcode()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);
        Assert.Equal(1, _slots.Count(_peer));

        using var back = _slots.TryEnter(_peer, 1, "one");

        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void A_read_with_no_play_session_has_no_transcode_to_close()
    {
        _slots.TryEnter(_peer, 1, null)!.Dispose();

        _clock.Advance(StreamSlots.IdleTimeout);

        Assert.Empty(_slots.TakeClosing());
    }

    [Fact]
    public void A_request_in_progress_never_expires()
    {
        Hold("one");
        using var request = _slots.TryEnter(_peer, 1, "one");

        _clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void The_idle_time_starts_when_the_last_request_ends()
    {
        Hold("one");
        var request = _slots.TryEnter(_peer, 1, "one");
        _clock.Advance(TimeSpan.FromHours(1));

        request!.Dispose();
        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Each_request_of_a_stream_renews_its_slot()
    {
        Hold("one");
        for (var segment = 0; segment < 5; segment++)
        {
            _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));
            _slots.TryEnter(_peer, 1, "one")!.Dispose();
        }

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_paused_stream_that_lost_its_slot_takes_a_free_one_when_it_resumes()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);

        using var request = _slots.TryEnter(_peer, 1, "one");

        Assert.NotNull(request);
    }

    [Fact]
    public void A_paused_stream_that_lost_its_slot_is_refused_if_another_took_it()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);
        Close();
        Hold("two");

        Assert.Null(_slots.TryEnter(_peer, 1, "one"));
    }

    [Fact]
    public void A_replacement_takes_over_the_slot_on_a_full_server()
    {
        Hold("one");

        var reservation = _slots.TryReserve(_peer, 1, "one");

        Assert.NotNull(reservation);
        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_replacement_can_only_free_a_slot_of_the_same_peer()
    {
        Hold("one");

        var reservation = _slots.TryReserve(_other, 1, "one");
        _slots.Release(_other, reservation!);

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void Releasing_an_unknown_session_does_nothing()
    {
        Hold("one");

        _slots.Release(_peer, "unknown");

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_renewal_restarts_the_idle_time()
    {
        Hold("one");

        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));
        _slots.Renew(_peer, "one");
        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public void A_slot_expires_once_the_renewals_stop()
    {
        Hold("one");
        _slots.Renew(_peer, "one");

        _clock.Advance(StreamSlots.IdleTimeout);
        Close();

        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void A_renewal_never_takes_a_slot()
    {
        _slots.Renew(_peer, "unknown");

        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void A_renewal_comes_too_late_for_a_slot_that_expired()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout);

        _slots.Renew(_peer, "one");
        Close();

        Assert.Equal(0, _slots.Count(_peer));
        Assert.NotNull(_slots.TryReserve(_peer, 1, null));
    }

    [Fact]
    public void A_renewal_only_reaches_the_slots_of_that_peer()
    {
        Hold("one");
        _clock.Advance(StreamSlots.IdleTimeout - TimeSpan.FromSeconds(1));

        _slots.Renew(_other, "one");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Close();

        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void Ending_a_request_twice_counts_once()
    {
        Hold("one");
        using var first = _slots.TryEnter(_peer, 1, "one");
        var second = _slots.TryEnter(_peer, 1, "one");

        second!.Dispose();
        second.Dispose();
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(1, _slots.Count(_peer));
    }

    [Fact]
    public async Task Requests_ending_while_others_start_stay_consistent()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                _slots.TryEnter(_peer, 64, null)?.Dispose();
            }
        })));

        Assert.Equal(0, _slots.Count(_peer));
    }

    [Fact]
    public void A_play_session_of_a_peer_runs_on_the_devices_it_was_confirmed_with()
    {
        var reservation = _slots.TryReserve(_peer, 1, null)!;
        _slots.Confirm(_peer, reservation, "play", _kept, ["peer-device", "Url Device+", null, string.Empty]);

        Assert.True(_slots.IsDeviceOfAnother(_other, "PEER-DEVICE"));
        Assert.True(_slots.IsDeviceOfAnother(_other, "url device+"));
        Assert.True(_slots.IsDeviceOfAnother(Guid.Empty, "peer-device"));
        Assert.False(_slots.IsDeviceOfAnother(_peer, "peer-device"));
        Assert.False(_slots.IsDeviceOfAnother(_other, "unknown-device"));
    }

    [Fact]
    public void The_devices_of_a_peer_outlive_its_slot()
    {
        var reservation = _slots.TryReserve(_peer, 1, null)!;
        _slots.Confirm(_peer, reservation, "play", _kept, ["peer-device"]);

        _slots.Release(_peer, "play");
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(0, _slots.Count(_peer));
        Assert.True(_slots.IsDeviceOfAnother(_other, "peer-device"));
    }

    [Fact]
    public void The_play_sessions_of_other_peers_outlive_their_slots()
    {
        var reservation = _slots.TryReserve(_peer, 1, null)!;
        _slots.Confirm(_peer, reservation, "play", _kept, ["peer-device"]);

        _slots.Release(_peer, "play");

        Assert.Equal(["play"], _slots.SessionsOfOthers(_other));
        Assert.Equal(["play"], _slots.SessionsOfOthers(Guid.Empty));
        Assert.Empty(_slots.SessionsOfOthers(_peer));
    }

    [Fact]
    public void A_play_session_of_a_peer_belongs_to_anyone_else_and_not_to_the_peer()
    {
        var reservation = _slots.TryReserve(_peer, 1, null)!;
        _slots.Confirm(_peer, reservation, "play", _kept, ["peer-device"]);

        Assert.True(_slots.IsOfAnother(_other, "play"));
        Assert.True(_slots.IsOfAnother(_other, "PLAY"));
        Assert.True(_slots.IsOfAnother(Guid.Empty, "play"));
        Assert.False(_slots.IsOfAnother(_peer, "play"));
        Assert.False(_slots.IsOfAnother(_other, "unknown"));
        Assert.False(_slots.IsOfAnother(_other, null));
    }

    [Fact]
    public void A_play_session_of_a_peer_stays_its_own_after_its_slot()
    {
        var reservation = _slots.TryReserve(_peer, 1, null)!;
        _slots.Confirm(_peer, reservation, "play", _kept);

        _slots.Release(_peer, "play");
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.True(_slots.IsOfAnother(_other, "play"));
    }

    [Fact]
    public void A_reservation_that_was_never_confirmed_belongs_to_nobody()
    {
        var reservation = _slots.TryReserve(_peer, 1, null)!;

        Assert.False(_slots.IsOfAnother(_other, reservation));
    }

    [Fact]
    public void A_reservation_that_was_never_confirmed_runs_on_no_device()
    {
        var reservation = _slots.TryReserve(_peer, 1, null)!;
        _slots.Release(_peer, reservation);

        Assert.False(_slots.IsDeviceOfAnother(_other, "peer-device"));
    }
}
