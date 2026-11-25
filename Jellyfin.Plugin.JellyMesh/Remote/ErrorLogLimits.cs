using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Holds the limits of the whole errors written to the log: one for each server and three for what belongs to none.
/// </summary>
public sealed class ErrorLogLimits
{
    private readonly ConcurrentDictionary<Guid, ErrorLogLimit> _servers = new();
    private readonly TimeProvider? _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorLogLimits"/> class.
    /// </summary>
    /// <param name="clock">The clock.</param>
    public ErrorLogLimits(TimeProvider? clock = null)
    {
        _clock = clock;
        Unsaved = new ErrorLogLimit(clock);
        Playback = new ErrorLogLimit(clock);
        Local = new ErrorLogLimit(clock);
    }

    /// <summary>
    /// Gets the limit of the logins and logouts of the servers that are not saved: new ones and removed ones.
    /// </summary>
    internal ErrorLogLimit Unsaved { get; }

    /// <summary>
    /// Gets the limit of the playback errors of no server, such as those of Jellyfin.
    /// </summary>
    internal ErrorLogLimit Playback { get; }

    /// <summary>
    /// Gets the limit of what is neither of a server nor of playback, such as the libraries, the index and the sync.
    /// </summary>
    internal ErrorLogLimit Local { get; }

    /// <summary>
    /// Gets the limit of the calls, logins and streams of a saved server.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <returns>The limit.</returns>
    internal ErrorLogLimit For(Guid serverId) => _servers.GetOrAdd(serverId, _ => new ErrorLogLimit(_clock));
}
