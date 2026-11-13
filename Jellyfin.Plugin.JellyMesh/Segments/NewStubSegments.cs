using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Segments;

/// <summary>
/// Asks Jellyfin for the media segments of a new stub once its first refresh has read the <c>.strm</c>, instead of
/// waiting for the scheduled segment scan (Q256). One stub at a time.
/// </summary>
public sealed class NewStubSegments : BackgroundService
{
    /// <summary>
    /// The most new stubs kept waiting; more wait for the scheduled segment scan.
    /// </summary>
    public const int MaxPending = 10_000;

    private readonly ILibraryManager _library;
    private readonly IMediaSegmentManager _manager;
    private readonly IStubRegistry _registry;
    private readonly StubSegments _segments;
    private readonly ILogger<NewStubSegments> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _awaitingRefresh = new();
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(MaxPending)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    /// <summary>
    /// Initializes a new instance of the <see cref="NewStubSegments"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="manager">The segment manager.</param>
    /// <param name="registry">The stub registry.</param>
    /// <param name="segments">The segments of the stubs.</param>
    /// <param name="logger">The logger.</param>
    public NewStubSegments(ILibraryManager library, IMediaSegmentManager manager, IStubRegistry registry, StubSegments segments, ILogger<NewStubSegments> logger)
    {
        _library = library;
        _manager = manager;
        _registry = registry;
        _segments = segments;
        _logger = logger;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _library.ItemUpdated += OnItemUpdated;
        _library.ItemRemoved += OnItemRemoved;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemUpdated -= OnItemUpdated;
        _library.ItemRemoved -= OnItemRemoved;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var itemId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunAsync(itemId, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    // A stub is new when the scan adds it; Jellyfin reads its .strm only in the refresh that follows.
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Video video
            && video.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true
            && _awaitingRefresh.Count < MaxPending
            && _registry.FindByPath(video.Path) is not null)
        {
            _awaitingRefresh.TryAdd(video.Id, 0);
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        var itemId = e.Item.Id;
        if (_awaitingRefresh.ContainsKey(itemId) && _segments.Supports(itemId) && _awaitingRefresh.TryRemove(itemId, out _))
        {
            _queue.Writer.TryWrite(itemId);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e) => _awaitingRefresh.TryRemove(e.Item.Id, out _);

    private async Task RunAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (_library.GetItemById(itemId) is not { } item)
        {
            return;
        }

        try
        {
            await _manager.RunSegmentPluginProviders(item, _library.GetLibraryOptions(item), false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Could not compute the media segments of a new stub: {Error}", exception.GetType().Name);
        }
    }
}
