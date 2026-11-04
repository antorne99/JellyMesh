using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Provider;
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
