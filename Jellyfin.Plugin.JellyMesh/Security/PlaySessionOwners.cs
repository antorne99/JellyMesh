using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Who opened which play session. Stock Jellyfin stops a transcode for anyone who names its play session, so this is what
/// stands between one user and the stream of another. It holds identifiers and nothing else, in memory: a restart ends the
/// transcodes too. It has to outlive a stream, because the transcode of a finished or expired one still has to be closed.
/// An owner keeps its last <see cref="MaxPerOwner"/> play sessions, the least recently used going first, so whoever opens
/// sessions in a loop only pushes out its own.
/// </summary>
public sealed class PlaySessionOwners
{
    /// <summary>
    /// The most play sessions kept for one owner. Well above what a server plays between two closes of a session.
    /// </summary>
    public const int MaxPerOwner = 256;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Dictionary<string, long>> _owners = [];
    private long _uses;

    /// <summary>
    /// Records a play session.
    /// </summary>
    /// <param name="owner">The user who opened it.</param>
    /// <param name="playSessionId">The play session.</param>
    public void Add(Guid owner, string playSessionId)
    {
        lock (_lock)
        {
            if (!_owners.TryGetValue(owner, out var sessions))
            {
                // Stock compares play sessions ignoring case, so a check here must too.
                sessions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                _owners[owner] = sessions;
            }

            sessions[playSessionId] = ++_uses;
            if (sessions.Count > MaxPerOwner)
            {
                sessions.Remove(sessions.MinBy(pair => pair.Value).Key);
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
            if (playSessionId is null || !_owners.TryGetValue(owner, out var sessions) || !sessions.ContainsKey(playSessionId))
            {
                return false;
            }

            sessions[playSessionId] = ++_uses;
            return true;
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
}
