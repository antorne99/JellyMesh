using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// The concurrent streams of each peer, counted here because the consumer goes through stock routes and nothing it
/// claims can be trusted. A slot lives as long as this server sees it used: a request in progress holds it, a renewal
/// from the consumer extends it, and after <see cref="IdleTimeout"/> without either it is gone with its transcode, so a
/// consumer that vanishes leaves nothing behind. A request that ends badly with nothing else running leaves it only
/// <see cref="AbandonGrace"/>. In memory on purpose: a restart ends the streams too.
/// </summary>
public sealed class StreamSlots
{
    /// <summary>
    /// Time without requests after which a slot is free. Longer than the idle time of the consumer plus its sweep, so
    /// that a consumer that is alive always releases first.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Time a slot outlives a request that lost its connection or failed, with nothing else running on it. A seek also
    /// drops a connection, and the next one comes milliseconds later: this is what keeps the slot of that stream from
    /// going to another, and what leaves a retry its place. Long enough for a slow client to come back, short enough that
    /// a limit of one feels free at once.
    /// </summary>
    public static readonly TimeSpan AbandonGrace = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Dictionary<string, Slot>> _peers = [];
    private readonly Dictionary<Guid, HashSet<StreamRequest>> _running = [];
    private readonly List<(Guid PeerId, string PlaySessionId)> _closing = [];
    private readonly PlaySessionOwners _owners = new();
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
    /// Turns a reservation into the slot of a play session, and makes the play session the peer's.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="reservation">The reservation.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <param name="itemId">The item it plays.</param>
    public void Confirm(Guid peerId, string reservation, string playSessionId, Guid itemId = default)
    {
        lock (_lock)
        {
            var slots = SlotsOf(peerId);
            if (slots.Remove(reservation))
            {
                slots[playSessionId] = new Slot(_clock.GetUtcNow(), 0, false, itemId);
            }
        }

        _owners.Add(peerId, playSessionId);
    }

    /// <summary>
    /// Tells whether a play session is one a peer opened through its own <c>PlaybackInfo</c>. This outlives the slot: an
    /// expired stream can resume, and its transcode still has to be closed.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>Whether it is the peer's.</returns>
    public bool Owns(Guid peerId, string? playSessionId)
    {
        return _owners.Owns(peerId, playSessionId);
    }

    /// <summary>
    /// Records a stream request. A play session without a slot takes one: that is a paused stream resuming. A request with
    /// no play session holds a slot only while it runs.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="limit">The most slots of the peer.</param>
    /// <param name="playSessionId">The play session, null if the request has none.</param>
    /// <param name="itemId">The item it reads.</param>
    /// <param name="cut">What ends the request from outside.</param>
    /// <returns>The end of the request, to dispose. Null if the peer is at its limit.</returns>
    public StreamRequest? TryEnter(Guid peerId, int limit, string? playSessionId, Guid itemId = default, Action? cut = null)
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
                slot = new Slot(_clock.GetUtcNow(), 1, playSessionId is null, itemId);
                slots[key] = slot;
            }

            var request = new StreamRequest(playSessionId, itemId, cut, ended => Exit(peerId, key, slot, ended));
            RunningOf(peerId).Add(request);
            return request;
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
                slot.Abandoned = false;
                _owners.Owns(peerId, key);
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
    /// Frees every slot of a peer, for when it is removed or its limit goes down. What is running keeps running without one,
    /// and the next request of a stream that goes on takes a slot like any other. Its play sessions stay its own: they still
    /// have transcodes to close.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    public void ReleaseAll(Guid peerId)
    {
        lock (_lock)
        {
            SlotsOf(peerId).Clear();
        }
    }

    /// <summary>
    /// Ends what a peer may no longer read: its requests are cut, its slots go and its transcodes are closed. The rest of
    /// what it plays is left alone.
    /// </summary>
    /// <param name="peerId">The peer.</param>
    /// <param name="keeps">Tells whether the peer still reaches an item.</param>
    public void Revoke(Guid peerId, Func<Guid, bool> keeps)
    {
        ArgumentNullException.ThrowIfNull(keeps);

        // Asked outside the lock: it goes to the library. The configuration has changed by now, so no new stream gets in.
        List<Guid> items;
        lock (_lock)
        {
            items = SlotsOf(peerId).Values.Select(slot => slot.ItemId).Concat(RunningOf(peerId).Select(request => request.ItemId)).Distinct().ToList();
        }

        var lost = items.Where(item => !keeps(item)).ToHashSet();
        List<StreamRequest> cut;
        lock (_lock)
        {
            var slots = SlotsOf(peerId);
            foreach (var (key, slot) in slots.Where(pair => lost.Contains(pair.Value.ItemId)).ToList())
            {
                slots.Remove(key);
                if (!slot.EndsWithRequest)
                {
                    _closing.Add((peerId, key));
                }
            }

            cut = RunningOf(peerId).Where(request => lost.Contains(request.ItemId)).ToList();
            _closing.AddRange(cut.Where(request => request.PlaySessionId is not null).Select(request => (peerId, request.PlaySessionId!)));
        }

        foreach (var request in cut)
        {
            request.Cut?.Invoke();
        }
    }

    /// <summary>
    /// Takes the play sessions whose transcode has to be closed: those whose slot expired or was revoked. One that came
    /// back has a slot again, and its transcode is in use.
    /// </summary>
    /// <returns>The play sessions.</returns>
    public IReadOnlyList<string> TakeClosing()
    {
        lock (_lock)
        {
            foreach (var peerId in _peers.Keys.ToList())
            {
                SlotsOf(peerId);
            }

            var closing = _closing
                .Where(entry => !_peers.TryGetValue(entry.PeerId, out var slots) || !slots.ContainsKey(entry.PlaySessionId))
                .Select(entry => entry.PlaySessionId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            _closing.Clear();
            return closing;
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

    // Expired slots go whenever the peer is looked at, so that no timer is needed. Their transcodes go too, after the grace
    // or the idle time alike: with the connection gone nothing else would close them. A request can outlive the slot it
    // entered with, and while it runs its transcode is in use.
    private Dictionary<string, Slot> SlotsOf(Guid peerId)
    {
        if (!_peers.TryGetValue(peerId, out var slots))
        {
            slots = [];
            _peers[peerId] = slots;
        }

        var now = _clock.GetUtcNow();
        foreach (var (key, slot) in slots.Where(pair => pair.Value.IsExpired(now)).ToList())
        {
            slots.Remove(key);
            if (!slot.EndsWithRequest && !RunningOf(peerId).Any(request => request.PlaySessionId == key))
            {
                _closing.Add((peerId, key));
            }
        }

        return slots;
    }

    private HashSet<StreamRequest> RunningOf(Guid peerId)
    {
        if (!_running.TryGetValue(peerId, out var running))
        {
            running = [];
            _running[peerId] = running;
        }

        return running;
    }

    private void Exit(Guid peerId, string key, Slot slot, StreamRequest request)
    {
        lock (_lock)
        {
            RunningOf(peerId).Remove(request);
            slot.InFlight--;
            slot.LastUsed = _clock.GetUtcNow();

            // The last request to end decides: until then the stream is alive.
            slot.Abandoned = request.IsAbandoned;
            if (slot.EndsWithRequest)
            {
                SlotsOf(peerId).Remove(key);
            }
        }
    }

    private sealed class Slot
    {
        public Slot(DateTimeOffset lastUsed, int inFlight, bool endsWithRequest, Guid itemId = default)
        {
            LastUsed = lastUsed;
            InFlight = inFlight;
            EndsWithRequest = endsWithRequest;
            ItemId = itemId;
        }

        public DateTimeOffset LastUsed { get; set; }

        public int InFlight { get; set; }

        public bool EndsWithRequest { get; }

        public Guid ItemId { get; }

        public bool Abandoned { get; set; }

        public bool IsExpired(DateTimeOffset now)
        {
            return InFlight == 0 && now - LastUsed >= (Abandoned ? AbandonGrace : IdleTimeout);
        }
    }
}
