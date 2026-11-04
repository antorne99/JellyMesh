using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The concurrent streams of each peer, counted here because the consumer goes through stock routes and nothing it
/// claims can be trusted. A slot lives as long as this server sees it used: a request in progress holds it, a renewal
/// from the consumer extends it, and after <see cref="IdleTimeout"/> without either it is gone, so a consumer that
/// vanishes leaves nothing behind. In memory on purpose: a restart ends the streams too.
/// </summary>
public sealed class StreamSlots
{
    /// <summary>
    /// Time without requests after which a slot is free. Longer than the idle time of the consumer plus its sweep, so
    /// that a consumer that is alive always releases first.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Dictionary<string, Slot>> _peers = [];
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamSlots"/> class.
    /// </summary>
    /// <param name="clock">The clock.</param>
    public StreamSlots(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Holds a slot for a <c>PlaybackInfo</c> before it runs, because the play session is only known after it, and two
    /// at the same time must not both pass.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="limit">The most slots of the peer.</param>
    /// <param name="replaces">The play session this one replaces, if any. It frees its slot first.</param>
    /// <returns>The reservation, null if the peer is at its limit.</returns>
    public string? TryReserve(Guid peerId, int limit, string? replaces)
    {
        lock (_lock)
        {
            var slots = SlotsOf(peerId);
            if (replaces is not null)
            {
                slots.Remove(replaces);
            }

            if (slots.Count >= limit)
            {
                return null;
            }

            var reservation = Guid.NewGuid().ToString("N");
            slots[reservation] = new Slot(_clock.GetUtcNow(), 1, false);
            return reservation;
        }
    }

    /// <summary>
    /// Turns a reservation into the slot of a play session.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="reservation">The reservation.</param>
    /// <param name="playSessionId">The play session.</param>
    public void Confirm(Guid peerId, string reservation, string playSessionId)
    {
        lock (_lock)
        {
            var slots = SlotsOf(peerId);
            if (slots.Remove(reservation))
            {
                slots[playSessionId] = new Slot(_clock.GetUtcNow(), 0, false);
            }
        }
    }

    /// <summary>
    /// Records a stream request. A play session without a slot takes one: that is a paused stream resuming, or a stream
    /// that outlived a restart. A request with no play session holds a slot only while it runs.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="limit">The most slots of the peer.</param>
    /// <param name="playSessionId">The play session, null if the request has none.</param>
    /// <returns>The end of the request, to dispose. Null if the peer is at its limit.</returns>
    public IDisposable? TryEnter(Guid peerId, int limit, string? playSessionId)
    {
        lock (_lock)
        {
            var slots = SlotsOf(peerId);
            var key = playSessionId ?? Guid.NewGuid().ToString("N");
            if (slots.TryGetValue(key, out var slot))
            {
                slot.InFlight++;
            }
            else if (slots.Count >= limit)
            {
                return null;
            }
            else
            {
                slot = new Slot(_clock.GetUtcNow(), 1, playSessionId is null);
                slots[key] = slot;
            }

            return new Scope(this, peerId, key, slot);
        }
    }

    /// <summary>
    /// Restarts the idle time of a slot. The last bytes of a stream can leave this server long before a client has read
    /// them, and only the consumer can tell. Never takes a slot: one that expired is gone, and an unknown one is ignored.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="key">The play session.</param>
    public void Renew(Guid peerId, string key)
    {
        lock (_lock)
        {
            if (SlotsOf(peerId).TryGetValue(key, out var slot))
            {
                slot.LastUsed = _clock.GetUtcNow();
            }
        }
    }

    /// <summary>
    /// Frees a slot. An unknown one is ignored: it may have expired.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="key">The play session or reservation.</param>
    public void Release(Guid peerId, string key)
    {
        lock (_lock)
        {
            SlotsOf(peerId).Remove(key);
        }
    }

    /// <summary>
    /// Counts the slots of a peer.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <returns>The slots in use.</returns>
    public int Count(Guid peerId)
    {
        lock (_lock)
        {
            return SlotsOf(peerId).Count;
        }
    }

    // Expired slots go whenever the peer is looked at, so that no timer is needed.
    private Dictionary<string, Slot> SlotsOf(Guid peerId)
    {
        if (!_peers.TryGetValue(peerId, out var slots))
        {
            slots = [];
            _peers[peerId] = slots;
        }

        var now = _clock.GetUtcNow();
        foreach (var expired in slots.Where(pair => pair.Value.InFlight == 0 && now - pair.Value.LastUsed >= IdleTimeout).Select(pair => pair.Key).ToList())
        {
            slots.Remove(expired);
        }

        return slots;
    }

    private void Exit(Guid peerId, string key, Slot slot)
    {
        lock (_lock)
        {
            slot.InFlight--;
            slot.LastUsed = _clock.GetUtcNow();
            if (slot.EndsWithRequest)
            {
                SlotsOf(peerId).Remove(key);
            }
        }
    }

    private sealed class Slot
    {
        public Slot(DateTimeOffset lastUsed, int inFlight, bool endsWithRequest)
        {
            LastUsed = lastUsed;
            InFlight = inFlight;
            EndsWithRequest = endsWithRequest;
        }

        public DateTimeOffset LastUsed { get; set; }

        public int InFlight { get; set; }

        public bool EndsWithRequest { get; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly StreamSlots _owner;
        private readonly Guid _peerId;
        private readonly string _key;
        private readonly Slot _slot;
        private int _ended;

        public Scope(StreamSlots owner, Guid peerId, string key, Slot slot)
        {
            _owner = owner;
            _peerId = peerId;
            _key = key;
            _slot = slot;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _owner.Exit(_peerId, _key, _slot);
            }
        }
    }
}
