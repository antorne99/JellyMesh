using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The open sessions, in memory: a restart ends them, and the remote closes what is left on its own.
/// </summary>
public sealed class PlaybackSessionStore
{
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);

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
