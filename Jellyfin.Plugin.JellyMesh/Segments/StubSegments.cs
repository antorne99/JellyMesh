using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Segments;

/// <summary>
/// Reads the media segments of a stub from its best source and remembers which source they came from.
/// </summary>
public sealed class StubSegments
{
    /// <summary>
    /// How long a server that could not be reached is left alone.
    /// </summary>
    public static readonly TimeSpan OfflinePause = TimeSpan.FromMinutes(10);

    private readonly StubLookup _stubs;
    private readonly IFederationIndex _index;
    private readonly IRemoteServerRegistry _registry;
    private readonly IRemoteSegmentClient _client;
    private readonly IPluginConfigurationStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<StubSegments> _logger;
    private readonly ConcurrentDictionary<Guid, SegmentOrigin> _origins = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _pausedUntil = new();
    private volatile IndexItemLookup? _lookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubSegments"/> class.
    /// </summary>
    /// <param name="stubs">The stub lookup.</param>
    /// <param name="index">The federation index, for whether a version has segments and its duration.</param>
    /// <param name="registry">The remote servers.</param>
    /// <param name="client">The segment client.</param>
    /// <param name="store">The configuration, for the time limit of a remote call.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">The logger.</param>
    public StubSegments(
        StubLookup stubs,
        IFederationIndex index,
        IRemoteServerRegistry registry,
        IRemoteSegmentClient client,
        IPluginConfigurationStore store,
        TimeProvider time,
        ILogger<StubSegments> logger)
    {
        _stubs = stubs;
        _index = index;
        _registry = registry;
        _client = client;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Tells whether an item is a stub with a source.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for such a stub.</returns>
    public bool Supports(Guid itemId) => _stubs.Find(itemId) is { Sources.Count: > 0 };

    /// <summary>
    /// Finds the source the segments of a stub came from.
    /// </summary>
    /// <param name="itemId">The stub item.</param>
    /// <returns>The origin, null when none was read since the start.</returns>
    public SegmentOrigin? OriginOf(Guid itemId) => _origins.GetValueOrDefault(itemId);

    /// <summary>
    /// Remembers where the segments of an item came from.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="origin">The origin.</param>
    internal void Remember(Guid itemId, SegmentOrigin origin) => _origins[itemId] = origin;

    /// <summary>
    /// Forgets the origin of the segments of an item.
    /// </summary>
    /// <param name="itemId">The item.</param>
    public void Forget(Guid itemId) => _origins.TryRemove(itemId, out _);

    /// <summary>
    /// Reads the segments of the best source of a stub. A failed read keeps what the stub has, so that a remote that is
    /// briefly away does not wipe them.
    /// </summary>
    /// <param name="request">The request of the segment manager.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments for the stub.</returns>
    public async Task<IReadOnlyList<MediaSegmentDto>> GetAsync(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_stubs.Find(request.ItemId) is not { Sources.Count: > 0 } stub)
        {
            Forget(request.ItemId);
            return [];
        }

        var best = stub.Sources[0];
        var version = VersionOf(best);
        if (version?.HasSegments == false)
        {
            Forget(request.ItemId);
            return [];
        }

        if (_pausedUntil.TryGetValue(best.ServerId, out var until) && _time.GetUtcNow() < until)
        {
            return [.. request.ExistingSegments];
        }

        var result = await ReadAsync(best, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            if (result.Failure is RemoteFailure.Unreachable or RemoteFailure.Timeout or RemoteFailure.Suspended or RemoteFailure.AuthenticationFailed)
            {
                _pausedUntil[best.ServerId] = _time.GetUtcNow() + OfflinePause;
            }

            _logger.LogDebug("Cannot read the media segments of a stub from server {ServerId}: {Failure}", best.ServerId, result.Failure);
            return [.. request.ExistingSegments];
        }

        _pausedUntil.TryRemove(best.ServerId, out _);
        var segments = result.Value!;
        if (segments.Count == 0)
        {
            Forget(request.ItemId);
            return [];
        }

        Remember(request.ItemId, new SegmentOrigin(best, version?.RunTimeTicks));
        return segments
            .Select(segment => new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = request.ItemId,
                Type = segment.Type,
                StartTicks = segment.StartTicks,
                EndTicks = segment.EndTicks
            })
            .ToList();
    }

    /// <summary>
    /// Finds the version of a source in the index.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>The version, null when the index does not know it.</returns>
    public IndexedVersion? VersionOf(StubSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return CurrentLookup().Find(source.ServerId, source.ItemId)?.Versions
            .FirstOrDefault(version => string.Equals(version.MediaSourceId, source.MediaSourceId, StringComparison.Ordinal));
    }

    private async Task<RemoteResult<IReadOnlyList<RemoteSegment>>> ReadAsync(StubSource source, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_store.Read(PlaybackTimes.From).RemotePlaybackInfo);
        try
        {
            return await _registry.RunPlaybackCallAsync(
                source.ServerId,
                (endpoint, session, token) => _client.GetSegmentsAsync(endpoint, session, source.ItemId, token),
                limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteResult.Fail<IReadOnlyList<RemoteSegment>>(RemoteFailure.Timeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug("Reading the media segments of a stub failed: {Error}", exception.GetType().Name);
            return RemoteResult.Fail<IReadOnlyList<RemoteSegment>>(RemoteFailure.ServerError);
        }
    }

    private IndexItemLookup CurrentLookup()
    {
        var snapshot = _index.Snapshot;
        var lookup = _lookup;
        if (lookup is null || !ReferenceEquals(lookup.Snapshot, snapshot))
        {
            lookup = IndexItemLookup.Build(snapshot);
            _lookup = lookup;
        }

        return lookup;
    }
}
