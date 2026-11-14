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
    private readonly PlaySessionOwners _localOwners;
    private readonly ConcurrentDictionary<string, Guid> _localSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSessionStore"/> class.
    /// </summary>
    /// <param name="clock">The clock that times the local play sessions, the system clock if null.</param>
    public PlaybackSessionStore(TimeProvider? clock = null)
    {
        _localOwners = PlaySessionOwners.WithClock(clock ?? TimeProvider.System);
    }

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
    /// <param name="itemId">The stub it plays.</param>
    /// <param name="deviceIds">The devices it runs on: the one that opened it and the ones its transcoding URLs name.</param>
    public void RecordLocal(Guid userId, string playSessionId, Guid itemId, params string?[] deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var devices = deviceIds.Where(device => !string.IsNullOrEmpty(device)).Select(device => device!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _localOwners.Add(userId, playSessionId, itemId, devices);
    }

    /// <summary>
    /// Counts a request of its user on the stock play session of a stub, which keeps it from going idle.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="playSessionId">The play session.</param>
    public void UseLocal(Guid userId, string? playSessionId)
    {
        _localOwners.Owns(userId, playSessionId);
    }

    /// <summary>
    /// Forgets the stock play session of a stub its user ended.
    /// </summary>
    /// <param name="userId">The user who ended it.</param>
    /// <param name="playSessionId">The play session.</param>
    public void EndLocal(Guid userId, string? playSessionId)
    {
        _localOwners.Remove(userId, playSessionId);
        ForgetEndedSources();
    }

    /// <summary>
    /// Records the server whose file the local transcode of a play session reads.
    /// </summary>
    /// <param name="playSessionId">The play session, recorded with <see cref="RecordLocal"/>.</param>
    /// <param name="serverId">The server of the file.</param>
    public void RecordLocalSource(string playSessionId, Guid serverId)
    {
        ArgumentNullException.ThrowIfNull(playSessionId);
        _localSources[playSessionId] = serverId;
    }

    /// <summary>
    /// Lists the play sessions still open whose local transcode reads a file of a server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The play sessions.</returns>
    public IReadOnlyList<string> LocalSessionsReading(Guid serverId)
    {
        ForgetEndedSources();
        return _localSources.Where(pair => pair.Value == serverId).Select(pair => pair.Key).ToList();
    }

    /// <summary>
    /// Forgets the stock play sessions of stubs nobody used for the session retention, so a long pause keeps them.
    /// </summary>
    /// <param name="times">The playback times.</param>
    public void ForgetUnusedLocal(PlaybackTimes times)
    {
        ArgumentNullException.ThrowIfNull(times);
        _localOwners.ForgetIdle(times.SessionRetention);
        ForgetEndedSources();
    }

    /// <summary>
    /// Finds the stub a play session plays on this server, whoever opened it.
    /// </summary>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>The stub item, null if the play session is not one.</returns>
    public Guid? LocalItemOf(string? playSessionId)
    {
        return _localOwners.ItemOf(playSessionId);
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
    /// Tells whether the stock play session of a stub played on this server by someone else runs on a device. Stock stops
    /// every job of a device for a segment that names no play session. No device, or a blank one, names none.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="deviceId">The device, null if the request names none.</param>
    /// <returns>Whether another user played a stub here from it.</returns>
    public bool IsLocalDeviceOfAnother(Guid userId, string? deviceId)
    {
        return _localOwners.DeviceOfAnother(userId, deviceId);
    }

    /// <summary>
    /// Lists the stock play sessions of the stubs other users play on this server.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The play sessions of the other users.</returns>
    public IReadOnlyList<string> LocalSessionsOfOthers(Guid userId)
    {
        return _localOwners.SessionsOfOthers(userId);
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

    // Forgets the file servers of play sessions no longer recorded.
    private void ForgetEndedSources()
    {
        foreach (var playSessionId in _localSources.Keys.Where(playSessionId => _localOwners.ItemOf(playSessionId) is null))
        {
            _localSources.TryRemove(playSessionId, out _);
        }
    }
}
