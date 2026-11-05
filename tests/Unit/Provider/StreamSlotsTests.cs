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
    private readonly StreamSlots _slots;

    public StreamSlotsTests()
    {
        _slots = new StreamSlots(_clock);
    }

    private void Hold(string playSession, int limit = 1)
    {
        var reservation = _slots.TryReserve(_peer, limit, null);
        Assert.NotNull(reservation);
        _slots.Confirm(_peer, reservation, playSession);
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
        Assert.Equal(0, _slots.Count(_peer));

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
}
