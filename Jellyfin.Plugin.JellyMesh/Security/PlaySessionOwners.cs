using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Who opened which play session. Stock Jellyfin stops a transcode for anyone who names its play session, so this is what
/// stands between one user and the stream of another. It holds identifiers and nothing else, in memory: a restart ends the
/// transcodes too. It has to outlive a stream, because the transcode of a finished or expired one still has to be closed.
/// The record of the peers keeps the last <see cref="MaxPerOwner"/> play sessions of each, the least recently used going
/// first, so whoever opens sessions in a loop only pushes out its own. The record of the local users has no limit: a
/// session leaves it when its owner ends it or after it went unused for the idle time.
/// </summary>
public sealed class PlaySessionOwners
{
    /// <summary>
    /// The most play sessions kept for one owner. Well above what a server plays between two closes of a session.
    /// </summary>
    public const int MaxPerOwner = 256;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Dictionary<string, Entry>> _owners = [];
    private readonly int? _limit;
    private readonly TimeProvider _clock;
    private long _uses;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaySessionOwners"/> class, with <see cref="MaxPerOwner"/> sessions
    /// per owner.
    /// </summary>
    public PlaySessionOwners()
        : this(MaxPerOwner, TimeProvider.System)
    {
    }

    private PlaySessionOwners(int? limit, TimeProvider clock)
    {
        _limit = limit;
        _clock = clock;
    }

    /// <summary>
    /// Creates a record with no limit per owner, whose sessions leave when ended or idle.
    /// </summary>
    /// <param name="clock">The clock that times the last use of a session.</param>
    /// <returns>The record.</returns>
    public static PlaySessionOwners WithoutLimit(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new PlaySessionOwners(null, clock);
    }

    /// <summary>
    /// Records a play session.
    /// </summary>
    /// <param name="owner">The user who opened it.</param>
    /// <param name="playSessionId">The play session.</param>
    public void Add(Guid owner, string playSessionId)
    {
        Add(owner, playSessionId, null);
    }

    /// <summary>
    /// Records a play session and the item it plays.
    /// </summary>
    /// <param name="owner">The user who opened it.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <param name="itemId">The item it plays, null if it is not known.</param>
    public void Add(Guid owner, string playSessionId, Guid? itemId)
    {
        lock (_lock)
        {
            if (!_owners.TryGetValue(owner, out var sessions))
            {
                // Stock compares play sessions ignoring case, so a check here must too.
                sessions = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                _owners[owner] = sessions;
            }

            sessions[playSessionId] = new Entry(++_uses, _clock.GetUtcNow(), itemId);
            if (_limit is { } limit && sessions.Count > limit)
            {
                sessions.Remove(sessions.MinBy(pair => pair.Value.Use).Key);
            }
        }
    }

    /// <summary>
    /// Tells whether a user opened a play session, and counts that as a use of it.
    /// </summary>
    /// <param name="owner">The user.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>Whether it is theirs.</returns>
    public bool Owns(Guid owner, string? playSessionId)
    {
        lock (_lock)
        {
            if (playSessionId is null || !_owners.TryGetValue(owner, out var sessions) || !sessions.TryGetValue(playSessionId, out var entry))
            {
                return false;
            }

            sessions[playSessionId] = entry with { Use = ++_uses, LastUse = _clock.GetUtcNow() };
            return true;
        }
    }

    /// <summary>
    /// Takes out a play session its owner ended.
    /// </summary>
    /// <param name="owner">The user who ended it.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>Whether it was theirs and is now gone.</returns>
    public bool Remove(Guid owner, string? playSessionId)
    {
        lock (_lock)
        {
            if (playSessionId is null || !_owners.TryGetValue(owner, out var sessions) || !sessions.Remove(playSessionId))
            {
                return false;
            }

            if (sessions.Count == 0)
            {
                _owners.Remove(owner);
            }

            return true;
        }
    }

    /// <summary>
    /// Takes out every play session nobody used for the idle time.
    /// </summary>
    /// <param name="idle">The idle time.</param>
    public void ForgetIdle(TimeSpan idle)
    {
        var oldest = _clock.GetUtcNow() - idle;
        lock (_lock)
        {
            foreach (var (owner, sessions) in _owners.ToList())
            {
                foreach (var (playSessionId, entry) in sessions.ToList())
                {
                    if (entry.LastUse <= oldest)
                    {
                        sessions.Remove(playSessionId);
                    }
                }

                if (sessions.Count == 0)
                {
                    _owners.Remove(owner);
                }
            }
        }
    }

    /// <summary>
    /// Tells whether a play session is known to belong to someone else.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>Whether another user opened it.</returns>
    public bool BelongsToAnother(Guid user, string? playSessionId)
    {
        if (playSessionId is null)
        {
            return false;
        }

        lock (_lock)
        {
            return _owners.Any(pair => !pair.Key.Equals(user) && pair.Value.ContainsKey(playSessionId));
        }
    }

    /// <summary>
    /// Finds the item a play session was recorded with, whoever opened it.
    /// </summary>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>The item, null if the play session or its item is not known.</returns>
    public Guid? ItemOf(string? playSessionId)
    {
        if (playSessionId is null)
        {
            return null;
        }

        lock (_lock)
        {
            foreach (var sessions in _owners.Values)
            {
                if (sessions.TryGetValue(playSessionId, out var entry) && entry.Item is { } item)
                {
                    return item;
                }
            }

            return null;
        }
    }

    private readonly record struct Entry(long Use, DateTimeOffset LastUse, Guid? Item);
}
