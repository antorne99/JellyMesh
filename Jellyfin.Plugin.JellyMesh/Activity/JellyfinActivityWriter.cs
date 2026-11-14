using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Activity;

/// <summary>
/// <see cref="IActivityWriter"/> over the activity log of Jellyfin, written from a queue so that no caller waits for its database.
/// </summary>
public sealed class JellyfinActivityWriter : IActivityWriter, IDisposable
{
    private const int Capacity = 256;

    private readonly IActivityManager _manager;
    private readonly ILogger<JellyfinActivityWriter> _logger;
    private readonly Channel<ActivityEntry> _queue = Channel.CreateBounded<ActivityEntry>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Task _drain;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinActivityWriter"/> class.
    /// </summary>
    /// <param name="manager">The activity manager of the server.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinActivityWriter(IActivityManager manager, ILogger<JellyfinActivityWriter> logger)
    {
        _manager = manager;
        _logger = logger;
        _drain = Task.Run(DrainAsync);
    }

    /// <inheritdoc />
    public void Write(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _queue.Writer.TryWrite(entry);
    }

    /// <inheritdoc />
    public void Dispose() => _queue.Writer.TryComplete();

    // Stops taking entries and ends once those already queued are written.
    internal Task StopAsync()
    {
        _queue.Writer.TryComplete();
        return _drain;
    }

    // Writes the queued entries one at a time, in order.
    private async Task DrainAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var log = new ActivityLog(entry.Name, entry.Type, Guid.Empty)
            {
                ShortOverview = entry.ShortOverview,
                Overview = entry.Overview,
                LogSeverity = entry.Severity
            };

            try
            {
                await _manager.CreateAsync(log).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Also a cancellation: no token is passed, so it is not the caller's. The type only: a message could hold an address.
                _logger.LogWarning("Cannot write {Type} to the activity log: {Error}", entry.Type, exception.GetType().Name);
            }
        }
    }
}
