using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The open sessions, in memory: a restart ends them, and the remote closes what is left on its own.
/// </summary>
public sealed class PlaybackSessionStore
{
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);
    private readonly PlaySessionOwners _localOwners = new();

    /// <summary>
    /// Gets a copy of the sessions.
    /// </summary>
    public IReadOnlyList<PlaybackSession> All => _sessions.Values.ToList();

    /// <summary>
    /// Adds a session.
    /// </summary>
    /// <param name="session">The session.</param>
    public void Add(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _sessions[session.Id] = session;
    }

    /// <summary>
    /// Records the stock play session of a stub played on this server: the local transcode or the local copy. It has no
    /// session here, and stock would stop it for anyone who names it.
    /// </summary>
    /// <param name="userId">The user who opened it.</param>
    /// <param name="playSessionId">The play session.</param>
    public void RecordLocal(Guid userId, string playSessionId)
    {
        _localOwners.Add(userId, playSessionId);
    }

    /// <summary>
    /// Tells whether a play session is a stub played on this server by someone else.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>Whether it belongs to another user.</returns>
    public bool IsLocalOfAnother(Guid userId, string? playSessionId)
    {
        return _localOwners.BelongsToAnother(userId, playSessionId);
    }

    /// <summary>
    /// Finds a session.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The session, null if there is none.</returns>
    public PlaybackSession? Find(string? sessionId)
    {
        return sessionId is not null && _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    /// <summary>
    /// Finds the session the same client has on the same title. A new <c>PlaybackInfo</c> replaces it: that is how a
    /// client changes the audio track, and the old transcode keeps running until it is closed.
    /// </summary>
    /// <param name="identity">Who plays what.</param>
    /// <returns>The session, null if there is none.</returns>
    public PlaybackSession? FindSameStream(PlaybackIdentity identity)
    {
        return _sessions.Values.FirstOrDefault(session => session.Identity == identity);
    }

    /// <summary>
    /// Takes a session out, so that no later request can reach it. A segment arriving after the close would restart
    /// ffmpeg on the remote.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The session, null if it was gone.</returns>
    public PlaybackSession? Remove(string sessionId)
    {
        return _sessions.TryRemove(sessionId, out var session) ? session : null;
    }
}
