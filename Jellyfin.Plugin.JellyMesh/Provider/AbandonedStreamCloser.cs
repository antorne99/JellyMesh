using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// Closes the transcodes <see cref="StreamSlots"/> has let go of, so that the limit of a peer also holds on the hardware of
/// this server. Most of it runs before a stream starts, so that the ffmpeg of the one before is gone by then, and the
/// timer is for when nobody asks. A consumer that comes back restarts ffmpeg with its next request, as after a pause.
/// </summary>
public sealed class AbandonedStreamCloser : BackgroundService
{
    /// <summary>
    /// How often slots are checked when no request does it.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    private readonly StreamSlots _slots;
    private readonly ITranscodeManager _transcodes;
    private readonly TimeProvider _clock;
    private readonly ILogger<AbandonedStreamCloser> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AbandonedStreamCloser"/> class.
    /// </summary>
    /// <param name="slots">The stream slots.</param>
    /// <param name="transcodes">The transcode manager.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public AbandonedStreamCloser(StreamSlots slots, ITranscodeManager transcodes, TimeProvider clock, ILogger<AbandonedStreamCloser> logger)
    {
        _slots = slots;
        _transcodes = transcodes;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Closes what has been let go of so far.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task CloseAbandonedAsync()
    {
        foreach (var playSessionId in _slots.TakeClosing())
        {
            await CloseAsync(playSessionId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the transcode of a play session, and what is left of it. Stock names a play session here, so no device.
    /// </summary>
    /// <param name="playSessionId">The play session.</param>
    /// <returns>A task.</returns>
    public async Task CloseAsync(string playSessionId)
    {
        try
        {
            await _transcodes.KillTranscodingJobs(string.Empty, playSessionId, _ => true).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // One transcode that cannot be closed must not stop the others, or the stream of whoever asked.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning("Could not close the transcode of a stream: {Error}", exception.GetType().Name);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(CheckInterval, _clock);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await CloseAbandonedAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
