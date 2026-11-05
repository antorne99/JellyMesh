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

    /// <summary>
    /// Most bytes of answers one read of one server may bring into memory, where they stay until it ends. Each page has its
    /// own limit, and only this stops many pages. An item takes around 10 kB and tens at most, so this holds a library of
    /// well over 100,000 items: a refused read leaves that server out of the federation, so this errs on the high side.
    /// </summary>
    public const long MaxBytesPerRead = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Most pages of the change feed in one read. A provider keeps 100,000 changes and sends 1,000 at a time, so a feed
    /// that goes on past this will never end.
    /// </summary>
    public const int MaxFeedPages = 1000;

    private const int MaxReadAttempts = 3;
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
    public async Task<RemoteResult<string>> GetFeedPositionAsync(RemoteServerInfo server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        var page = await ReadFeedPageAsync(server, null, cancellationToken).ConfigureAwait(false);
        return page.IsSuccess ? RemoteResult.Success(page.Value!.Cursor) : RemoteResult.Fail<string>(page.Failure);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<FeedRead>> ReadFeedAsync(
        RemoteServerInfo server,
        string cursor,
        IReadOnlySet<string> knownItemIds,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        var changes = new Dictionary<string, RemoteChange>(StringComparer.Ordinal);
        var budget = new ReadBudget();
        var position = cursor;
        var pages = 0;
        RemoteChangePage page;
        do
        {
            var read = await ReadFeedPageAsync(server, position, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return RemoteResult.Fail<FeedRead>(read.Failure);
            }

            page = read.Value!;
            if (page.Reset)
            {
                return RemoteResult.Success(new FeedRead(true, page.Cursor, []));
            }

            if (!budget.TrySpend(page.ByteCount))
            {
                return RemoteResult.Fail<FeedRead>(RemoteFailure.ResponseTooLarge);
            }

            // A cursor that stands still would never end, and neither would a feed that never stops growing, or one that
            // keeps moving over the same few items.
            if ((page.HasMore && page.Cursor == position) || ++pages > MaxFeedPages || changes.Count + page.Changes.Count > MaxItemsPerQuery)
            {
                return RemoteResult.Fail<FeedRead>(RemoteFailure.InvalidResponse);
            }

            position = page.Cursor;
            foreach (var change in page.Changes)
            {
                changes[change.Id] = change;
            }
        }
        while (page.HasMore);

        var entries = new Dictionary<string, IndexedItem>(StringComparer.Ordinal);
        var failure = await ReadChangedItemsAsync(server, changes.Values, knownItemIds, entries, budget, seenUtc, cancellationToken).ConfigureAwait(false);
        return failure == RemoteFailure.None
            ? RemoteResult.Success(new FeedRead(false, position, entries.Values.ToList()))
            : RemoteResult.Fail<FeedRead>(failure);
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

    private static bool ViaProvider(RemoteServerInfo server) => server.Mode == RemoteMode.JellyMesh;

    private Task<RemoteResult<RemoteChangePage>> ReadFeedPageAsync(RemoteServerInfo server, string? cursor, CancellationToken cancellationToken)
    {
        return _registry.RunWithSessionAsync(
            server.Id,
            (endpoint, session, token) => _client.GetChangesAsync(endpoint, session, cursor, token),
            cancellationToken);
    }

    // The feed names the items, and they are read by the same route as a full read, so the provider checks them again.
    private async Task<RemoteFailure> ReadChangedItemsAsync(
        RemoteServerInfo server,
        IEnumerable<RemoteChange> changes,
        IReadOnlySet<string> knownItemIds,
        Dictionary<string, IndexedItem> entries,
        ReadBudget budget,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken)
    {
        var wanted = changes
            .Select(change => (change.Id, Library: change.LibraryIds.FirstOrDefault(server.ImportedLibraryIds.Contains), Known: Enum.TryParse<RemoteItemType>(change.Type, out var type), Type: type))
            .Where(change => change.Library is not null && change.Known)
            .ToList();

        foreach (var group in wanted.GroupBy(change => (change.Library, change.Type)))
        {
            foreach (var chunk in group.Select(change => change.Id).Chunk(PageSize))
            {
                var query = new RemoteItemQuery(null, group.Key.Type, 0, chunk.Length, chunk, null, true);
                var page = await _registry.RunWithSessionAsync(
                    server.Id,
                    (endpoint, session, token) => _client.GetItemsAsync(endpoint, session, query, token),
                    cancellationToken).ConfigureAwait(false);
                if (!page.IsSuccess)
                {
                    return page.Failure;
                }

                if (!budget.TrySpend(page.Value!.ByteCount))
                {
                    return RemoteFailure.ResponseTooLarge;
                }

                Add(entries, page.Value.Items, group.Key.Type, group.Key.Library!, seenUtc);
            }
        }

        foreach (var library in wanted.Select(change => change.Library!).Distinct(StringComparer.Ordinal))
        {
            var failure = await AddMissingSeriesAsync(server, library, knownItemIds, entries, budget, seenUtc, cancellationToken).ConfigureAwait(false);
            if (failure != RemoteFailure.None)
            {
                return failure;
            }
        }

        return RemoteFailure.None;
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
        var budget = new ReadBudget();
        foreach (var library in chosen)
        {
            var failure = await ReadLibraryAsync(server, library, sinceUtc, knownItemIds, entries, budget, seenUtc, cancellationToken).ConfigureAwait(false);
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
        ReadBudget budget,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken)
    {
        var types = string.Equals(library.CollectionType, MoviesCollection, StringComparison.OrdinalIgnoreCase)
            ? new[] { RemoteItemType.Movie }
            : [RemoteItemType.Series, RemoteItemType.Season, RemoteItemType.Episode];

        foreach (var type in types)
        {
            var read = await ReadAllAsync(server, library.Id, type, sinceUtc, budget, cancellationToken).ConfigureAwait(false);
            if (!read.IsSuccess)
            {
                return read.Failure;
            }

            Add(entries, read.Value!, type, library.Id, seenUtc);
        }

        return types.Contains(RemoteItemType.Episode)
            ? await AddMissingSeriesAsync(server, library.Id, knownItemIds, entries, budget, seenUtc, cancellationToken).ConfigureAwait(false)
            : RemoteFailure.None;
    }

    // /Items collapses the rows of a series split across library paths into one (Phase 0, point 2), while each episode
    // keeps the ID of its own row. Without the row the episode would lose the IDs of its series.
    private async Task<RemoteFailure> AddMissingSeriesAsync(
        RemoteServerInfo server,
        string libraryId,
        IReadOnlySet<string> knownItemIds,
        Dictionary<string, IndexedItem> entries,
        ReadBudget budget,
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

        // One at a time, because even a lookup by ID answers with the row that represents the series (observed on 12.4).
        // It has the same IDs and name, so it is kept under the ID the episodes point to.
        foreach (var id in missing)
        {
            var query = new RemoteItemQuery(null, RemoteItemType.Series, 0, 1, [id], null, ViaProvider(server));
            var page = await _registry.RunWithSessionAsync(
                server.Id,
                (endpoint, session, token) => _client.GetItemsAsync(endpoint, session, query, token),
                cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                return page.Failure;
            }

            if (!budget.TrySpend(page.Value!.ByteCount))
            {
                return RemoteFailure.ResponseTooLarge;
            }

            Add(entries, page.Value.Items.Take(1).Select(item => item with { Id = id }), RemoteItemType.Series, libraryId, seenUtc);
        }

        var unresolved = missing.Count(id => !entries.ContainsKey(id));
        if (unresolved > 0)
        {
            _logger.LogWarning("Remote server {ServerName} has episodes of {Count} series it does not list", server.Name, unresolved);
        }

        return RemoteFailure.None;
    }

    private async Task<RemoteResult<IReadOnlyCollection<RemoteItem>>> ReadAllAsync(RemoteServerInfo server, string libraryId, RemoteItemType type, DateTimeOffset? sinceUtc, ReadBudget budget, CancellationToken cancellationToken)
    {
        // A second attempt replaces what the first one read, so it spends the same bytes again.
        var spent = budget.Spent;
        var read = RemoteResult.Fail<IReadOnlyCollection<RemoteItem>>(RemoteFailure.CatalogChanged);
        for (var attempt = 0; attempt < MaxReadAttempts && read.Failure == RemoteFailure.CatalogChanged; attempt++)
        {
            budget.Spent = spent;
            read = await ReadPagesAsync(server, libraryId, type, sinceUtc, budget, cancellationToken).ConfigureAwait(false);
        }

        return read;
    }

    // Pages are offsets: an item removed during the read shifts the next ones past the point reached, and one of them
    // would be skipped without a trace. So the read only counts if the total held still and every item was seen.
    private async Task<RemoteResult<IReadOnlyCollection<RemoteItem>>> ReadPagesAsync(RemoteServerInfo server, string libraryId, RemoteItemType type, DateTimeOffset? sinceUtc, ReadBudget budget, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, RemoteItem>(StringComparer.Ordinal);
        int? total = null;
        var limit = PageSize;
        var start = 0;
        do
        {
            var query = new RemoteItemQuery(libraryId, type, start, limit, null, sinceUtc, ViaProvider(server));
            var page = await _registry.RunWithSessionAsync(
                server.Id,
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

            if (!budget.TrySpend(page.Value!.ByteCount))
            {
                return RemoteResult.Fail<IReadOnlyCollection<RemoteItem>>(RemoteFailure.ResponseTooLarge);
            }

            if (page.Value.TotalRecordCount > MaxItemsPerQuery)
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

    private sealed class ReadBudget
    {
        public long Spent { get; set; }

        public bool TrySpend(long bytes)
        {
            Spent += bytes;
            return Spent <= MaxBytesPerRead;
        }
    }
}
