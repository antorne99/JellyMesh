using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
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
    private readonly IPluginConfigurationStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalRefreshRequester> _logger;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _requested = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalRefreshRequester"/> class.
    /// </summary>
    /// <param name="providers">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="store">The configuration, for the sync interval.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public LocalRefreshRequester(IProviderManager providers, IFileSystem fileSystem, IPluginConfigurationStore store, TimeProvider timeProvider, ILogger<LocalRefreshRequester> logger)
    {
        _providers = providers;
        _fileSystem = fileSystem;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Request(IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var now = _timeProvider.GetUtcNow();
        var fresh = itemIds.Where(id => _requested.TryAdd(id, now)).ToList();
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

    /// <inheritdoc />
    public bool IsOverdue(Guid itemId)
    {
        if (!_requested.TryGetValue(itemId, out var asked))
        {
            return false;
        }

        // The interval the sync really runs at, which is never below the minimum.
        var seconds = Math.Max(_store.Read(config => config.SyncIntervalSeconds), PluginConfiguration.MinSyncIntervalSeconds);
        return _timeProvider.GetUtcNow() - asked >= TimeSpan.FromSeconds(seconds);
    }
}
