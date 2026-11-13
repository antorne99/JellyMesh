using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Closes what clients leave open: on a reported stop right away, within a second when the source of a session dropped,
/// after the grace on a JellyMesh remote when a client dropped its connection, and after the idle time when the client
/// simply went away. Nothing pings the remote during a pause: its job ends, and resuming restarts it. Also forgets the
/// stock play session of a stub when its user stops it or leaves it idle.
/// </summary>
public sealed class PlaybackSweeper : BackgroundService
{
    private readonly PlaybackCoordinator _coordinator;
    private readonly ISessionManager _sessionManager;
    private readonly PlaybackSessionStore _sessions;
    private readonly IPluginConfigurationStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlaybackSweeper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSweeper"/> class.
    /// </summary>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="sessions">The sessions.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackSweeper(
        PlaybackCoordinator coordinator,
        ISessionManager sessionManager,
        PlaybackSessionStore sessions,
        IPluginConfigurationStore store,
        TimeProvider clock,
        ILogger<PlaybackSweeper> logger)
    {
        _coordinator = coordinator;
        _sessionManager = sessionManager;
        _sessions = sessions;
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _coordinator.CloseAllAsync().WaitAsync(PlaybackTimeouts.Shutdown, _clock, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Shutting down with remote transcodes still open");
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        try
        {
            await Task.WhenAll(SweepAsync(stoppingToken), ReleaseAbandonedAsync(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PlaybackTimeouts.SweepInterval, _clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                _sessions.ForgetUnusedLocal(_store.Read(PlaybackTimes.From));
                await _coordinator.SweepAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One failed pass must not end the sweeps for good.
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                // The type only: a message could hold a remote address.
                _logger.LogError("Could not sweep the playback sessions: {Error}", exception.GetType().Name);
            }
        }
    }

    // A loop of its own: a remote that does not answer holds a sweep up, and this has to come right after the grace or
    // the drop.
    private async Task ReleaseAbandonedAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PlaybackTimeouts.AbandonCheck, _clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await Task.WhenAll(_coordinator.ReleaseAbandonedAsync(), _coordinator.CloseFailedAsync()).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // This only saves time on a remote, and must not end the loop or touch a stream.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                // The type only: a message could hold a remote address.
                _logger.LogError("Could not tell a remote that a client went or a source dropped: {Error}", exception.GetType().Name);
            }
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.PlaySessionId is { } playSessionId)
        {
            foreach (var user in e.Users)
            {
                _sessions.EndLocal(user.Id, playSessionId);
            }

            _ = CloseAsync(playSessionId, e.Users.Select(user => user.Id).ToList());
        }
    }

    private async Task CloseAsync(string playSessionId, IReadOnlyList<Guid> users)
    {
        try
        {
            await _coordinator.CloseReportedAsync(playSessionId, users).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Runs detached from the event, so nothing above it could handle a failure.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // The type only: a message could hold a remote address.
            _logger.LogError("Could not close a playback session: {Error}", exception.GetType().Name);
        }
    }
}
