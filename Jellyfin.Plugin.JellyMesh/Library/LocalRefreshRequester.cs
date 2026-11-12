using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILocalRefreshRequester"/> through the provider manager, with the options of the Dashboard's refresh of one
/// item. Each item is asked for once while the server runs, so an item that never gets refreshed is not asked for again.
/// </summary>
public sealed class LocalRefreshRequester : ILocalRefreshRequester
{
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<LocalRefreshRequester> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _requested = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalRefreshRequester"/> class.
    /// </summary>
    /// <param name="providers">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public LocalRefreshRequester(IProviderManager providers, IFileSystem fileSystem, ILogger<LocalRefreshRequester> logger)
    {
        _providers = providers;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Request(IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var fresh = itemIds.Where(id => _requested.TryAdd(id, 0)).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        foreach (var itemId in fresh)
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default,
                IsAutomated = false
            };
            _providers.QueueRefresh(itemId, options, RefreshPriority.High);
        }

        _logger.LogInformation("Asked Jellyfin to read the metadata of {Count} new local films, so that they can be matched", fresh.Count);
    }
}
