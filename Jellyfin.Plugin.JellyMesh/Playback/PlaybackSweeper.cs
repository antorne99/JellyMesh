using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Closes what clients leave open: on a reported stop right away, and after the idle time when the client simply went
/// away. Nothing pings the remote during a pause: its job ends, and resuming restarts it.
/// </summary>
public sealed class PlaybackSweeper : BackgroundService
{
    private readonly PlaybackCoordinator _coordinator;
    private readonly ISessionManager _sessionManager;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlaybackSweeper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackSweeper"/> class.
    /// </summary>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public PlaybackSweeper(PlaybackCoordinator coordinator, ISessionManager sessionManager, TimeProvider clock, ILogger<PlaybackSweeper> logger)
    {
        _coordinator = coordinator;
        _sessionManager = sessionManager;
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
            using var timer = new PeriodicTimer(PlaybackTimeouts.SweepInterval, _clock);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await _coordinator.SweepAsync().ConfigureAwait(false);
            }
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

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.PlaySessionId is { } playSessionId)
        {
            _ = CloseAsync(playSessionId);
        }
    }

    private async Task CloseAsync(string playSessionId)
    {
        try
        {
            await _coordinator.CloseAsync(playSessionId).ConfigureAwait(false);
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
