using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Activity;

/// <summary>
/// <see cref="IActivityWriter"/> over the activity log of Jellyfin.
/// </summary>
public sealed class JellyfinActivityWriter : IActivityWriter
{
    private readonly IActivityManager _manager;
    private readonly ILogger<JellyfinActivityWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinActivityWriter"/> class.
    /// </summary>
    /// <param name="manager">The activity manager of the server.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinActivityWriter(IActivityManager manager, ILogger<JellyfinActivityWriter> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

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
