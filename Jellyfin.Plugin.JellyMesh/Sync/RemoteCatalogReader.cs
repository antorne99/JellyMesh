using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// <see cref="IRemoteCatalogReader"/> over paged <c>/Items</c> calls. Each page goes through the registry, so a token that
/// expires during a long read is renewed, and a suspended key stops the read.
/// </summary>
public sealed class RemoteCatalogReader : IRemoteCatalogReader
{
    /// <summary>
    /// Items asked for per page. An item with many streams and people weighs tens of kilobytes, so a page stays far from
    /// <see cref="JellyfinRemoteClient.MaxResponseBytes"/>, and the limit is halved if one does not.
    /// </summary>
    public const int PageSize = 200;

    /// <summary>
    /// Most items accepted for one query, so that a remote cannot fill the memory of this server.
    /// </summary>
    public const int MaxItemsPerQuery = 1_000_000;

    private const int MaxReadAttempts = 3;
    private const int MaxIdsPerLookup = 50;
    private const string MoviesCollection = "movies";

    private readonly IRemoteServerRegistry _registry;
    private readonly IJellyfinRemoteClient _client;
    private readonly ILogger<RemoteCatalogReader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteCatalogReader"/> class.
    /// </summary>
    /// <param name="registry">The server registry.</param>
    /// <param name="client">The remote client.</param>
    /// <param name="logger">The logger.</param>
    public RemoteCatalogReader(IRemoteServerRegistry registry, IJellyfinRemoteClient client, ILogger<RemoteCatalogReader> logger)
    {
        _registry = registry;
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<RemoteResult<IReadOnlyList<IndexedItem>>> ReadAsync(RemoteServerInfo server, DateTimeOffset seenUtc, CancellationToken cancellationToken)
    {
        return ReadEntriesAsync(server, null, new HashSet<string>(), seenUtc, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RemoteResult<IReadOnlyList<IndexedItem>>> ReadChangesAsync(
        RemoteServerInfo server,
        DateTimeOffset sinceUtc,
        IReadOnlySet<string> knownItemIds,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken)
    {
        return ReadEntriesAsync(server, sinceUtc, knownItemIds, seenUtc, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RemoteResult<DateTimeOffset?>> GetServerTimeAsync(RemoteServerInfo server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.ImportedLibraryIds.Count == 0
            ? Task.FromResult(RemoteResult.Success<DateTimeOffset?>(null))
            : _registry.RunWithSessionAsync(server.Id, _client.GetServerTimeAsync, cancellationToken);
    }

    private static void Add(Dictionary<string, IndexedItem> entries, IEnumerable<RemoteItem> items, RemoteItemType type, string libraryId, DateTimeOffset seenUtc)
    {
        foreach (var item in items)
        {
            entries[item.Id] = RemoteItemMapper.ToIndexedItem(item, type, libraryId, seenUtc);
        }
    }

    private async Task<RemoteResult<IReadOnlyList<IndexedItem>>> ReadEntriesAsync(
        RemoteServerInfo server,
        DateTimeOffset? sinceUtc,
        IReadOnlySet<string> knownItemIds,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.ImportedLibraryIds.Count == 0)
        {
            return RemoteResult.Success<IReadOnlyList<IndexedItem>>([]);
        }

        var libraries = await _registry.GetLibrariesAsync(server.Id, cancellationToken).ConfigureAwait(false);
        if (!libraries.IsSuccess)
        {
            return RemoteResult.Fail<IReadOnlyList<IndexedItem>>(libraries.Failure);
        }

        var chosen = libraries.Value!.Where(library => server.ImportedLibraryIds.Contains(library.Id)).ToList();
        if (chosen.Count != server.ImportedLibraryIds.Count)
        {
            return RemoteResult.Fail<IReadOnlyList<IndexedItem>>(RemoteFailure.LibraryUnavailable);
        }

        var entries = new Dictionary<string, IndexedItem>(StringComparer.Ordinal);
        foreach (var library in chosen)
        {
            var failure = await ReadLibraryAsync(server, library, sinceUtc, knownItemIds, entries, seenUtc, cancellationToken).ConfigureAwait(false);
            if (failure != RemoteFailure.None)
            {
                return RemoteResult.Fail<IReadOnlyList<IndexedItem>>(failure);
            }
        }

        return RemoteResult.Success<IReadOnlyList<IndexedItem>>(entries.Values.ToList());
    }

    private async Task<RemoteFailure> ReadLibraryAsync(
        RemoteServerInfo server,
        RemoteLibrary library,
        DateTimeOffset? sinceUtc,
        IReadOnlySet<string> knownItemIds,
        Dictionary<string, IndexedItem> entries,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken)
    {
        var types = string.Equals(library.CollectionType, MoviesCollection, StringComparison.OrdinalIgnoreCase)
            ? new[] { RemoteItemType.Movie }
            : [RemoteItemType.Series, RemoteItemType.Season, RemoteItemType.Episode];

        foreach (var type in types)
        {
            var read = await ReadAllAsync(server.Id, library.Id, type, sinceUtc, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return read.Failure;
            }

            Add(entries, read.Value!, type, library.Id, seenUtc);
        }

        return types.Contains(RemoteItemType.Episode)
            ? await AddMissingSeriesAsync(server, library, knownItemIds, entries, seenUtc, cancellationToken).ConfigureAwait(false)
            : RemoteFailure.None;
    }

    // /Items collapses the rows of a series split across library paths into one (Phase 0, point 2), while each episode
    // keeps the ID of its own row. Without the row the episode would lose the IDs of its series.
    private async Task<RemoteFailure> AddMissingSeriesAsync(
        RemoteServerInfo server,
        RemoteLibrary library,
        IReadOnlySet<string> knownItemIds,
        Dictionary<string, IndexedItem> entries,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken)
    {
        var missing = entries.Values
            .Where(entry => entry.Kind == IndexedItemKind.Episode)
            .Select(entry => entry.SeriesId)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(id => !entries.ContainsKey(id) && !knownItemIds.Contains(id))
            .ToList();

        foreach (var ids in missing.Chunk(MaxIdsPerLookup))
        {
            var query = new RemoteItemQuery(null, RemoteItemType.Series, 0, ids.Length, ids);
            var page = await _registry.RunWithSessionAsync(
                server.Id,
                (endpoint, session, token) => _client.GetItemsAsync(endpoint, session, query, token),
                cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                return page.Failure;
            }

            Add(entries, page.Value!.Items, RemoteItemType.Series, library.Id, seenUtc);
        }

        var unresolved = missing.Count(id => !entries.ContainsKey(id));
        if (unresolved > 0)
        {
            _logger.LogWarning("Remote server {ServerName} has episodes of {Count} series it does not list", server.Name, unresolved);
        }

        return RemoteFailure.None;
    }

    private async Task<RemoteResult<IReadOnlyCollection<RemoteItem>>> ReadAllAsync(Guid serverId, string libraryId, RemoteItemType type, DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
    {
        var read = RemoteResult.Fail<IReadOnlyCollection<RemoteItem>>(RemoteFailure.CatalogChanged);
        for (var attempt = 0; attempt < MaxReadAttempts && read.Failure == RemoteFailure.CatalogChanged; attempt++)
        {
            read = await ReadPagesAsync(serverId, libraryId, type, sinceUtc, cancellationToken).ConfigureAwait(false);
        }

        return read;
    }

    // Pages are offsets: an item removed during the read shifts the next ones past the point reached, and one of them
    // would be skipped without a trace. So the read only counts if the total held still and every item was seen.
    private async Task<RemoteResult<IReadOnlyCollection<RemoteItem>>> ReadPagesAsync(Guid serverId, string libraryId, RemoteItemType type, DateTimeOffset? sinceUtc, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, RemoteItem>(StringComparer.Ordinal);
        int? total = null;
        var limit = PageSize;
        var start = 0;
        do
        {
            var query = new RemoteItemQuery(libraryId, type, start, limit, null, sinceUtc);
            var page = await _registry.RunWithSessionAsync(
                serverId,
                (endpoint, session, token) => _client.GetItemsAsync(endpoint, session, query, token),
                cancellationToken).ConfigureAwait(false);

            if (page.Failure == RemoteFailure.ResponseTooLarge && limit > 1)
            {
                limit /= 2;
                continue;
            }

            if (!page.IsSuccess)
            {
                return RemoteResult.Fail<IReadOnlyCollection<RemoteItem>>(page.Failure);
            }

            if (page.Value!.TotalRecordCount > MaxItemsPerQuery)
            {
                return RemoteResult.Fail<IReadOnlyCollection<RemoteItem>>(RemoteFailure.InvalidResponse);
            }

            if ((total ?? page.Value.TotalRecordCount) != page.Value.TotalRecordCount
                || (page.Value.Items.Count == 0 && start < page.Value.TotalRecordCount))
            {
                return RemoteResult.Fail<IReadOnlyCollection<RemoteItem>>(RemoteFailure.CatalogChanged);
            }

            total = page.Value.TotalRecordCount;
            start += page.Value.Items.Count;
            foreach (var item in page.Value.Items)
            {
                items.TryAdd(item.Id, item);
            }
        }
        while (total is null || start < total);

        return items.Count == total
            ? RemoteResult.Success<IReadOnlyCollection<RemoteItem>>(items.Values)
            : RemoteResult.Fail<IReadOnlyCollection<RemoteItem>>(RemoteFailure.CatalogChanged);
    }
}
