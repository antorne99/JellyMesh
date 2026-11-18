using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IWatchStateKeeper"/> over the user data of Jellyfin and a <see cref="IKeptWatchStateStore"/>. The identity
/// of a title is that of the dedup (plan §4.1), read from the library item of the old stub, since the index has dropped
/// the removed server by the time its stubs go.
/// </summary>
public sealed class WatchStateKeeper : IWatchStateKeeper, IDisposable
{
    private const string SavedServer = "kept";

    // How long a record nobody used is kept (row 412).
    private static readonly TimeSpan _keptFor = TimeSpan.FromDays(90);

    private readonly StubRegistry _registry;
    private readonly IFederationIndex _index;
    private readonly IRemoteServerRegistry _servers;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IKeptWatchStateStore _kept;
    private readonly IManualMergeStore _merges;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WatchStateKeeper> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The servers whose last save failed, in memory only: a restart lists them again at the next failed run.
    private readonly ConcurrentDictionary<Guid, byte> _held = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStateKeeper"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="index">The index, for the identity of the current stubs.</param>
    /// <param name="servers">The server registry.</param>
    /// <param name="store">The configuration, for the federated folder.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="kept">Where the state is kept.</param>
    /// <param name="merges">The manual merges, which also join kept titles (D21).</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public WatchStateKeeper(
        StubRegistry registry,
        IFederationIndex index,
        IRemoteServerRegistry servers,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        IKeptWatchStateStore kept,
        IManualMergeStore merges,
        TimeProvider timeProvider,
        ILogger<WatchStateKeeper> logger)
    {
        _registry = registry;
        _index = index;
        _servers = servers;
        _store = store;
        _library = library;
        _users = users;
        _userData = userData;
        _kept = kept;
        _merges = merges;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<HeldRemoval> HeldRemovals
    {
        get
        {
            // Only servers still removed and with titles: one configured again, or whose titles went, is held no more.
            var state = _registry.State;
            var configured = _servers.GetServers().Select(server => server.Id).ToHashSet();
            return _held.Keys
                .Where(serverId => state.RemovedServers.Contains(serverId) && !configured.Contains(serverId) && state.HasTitlesOf(serverId))
                .Select(serverId => new HeldRemoval(state.ServerNames.GetValueOrDefault(serverId)))
                .OrderBy(held => held.Name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <inheritdoc />
    public async Task<bool> KeepAsync(string root, IReadOnlyCollection<StubEntry> stubs, IReadOnlyCollection<SeriesStub> series, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stubs);
        ArgumentNullException.ThrowIfNull(series);
        if (stubs.Count == 0 && series.Count == 0)
        {
            return true;
        }

        var kept = await SaveAsync(root, stubs, series, cancellationToken).ConfigureAwait(false);
        foreach (var serverId in ServersOf(stubs, series))
        {
            if (kept)
            {
                _held.TryRemove(serverId, out _);
            }
            else
            {
                _held.TryAdd(serverId, 0);
            }
        }

        return kept;
    }

    /// <inheritdoc />
    public async Task<bool> KeepMergesAsync(Guid serverId, IReadOnlyList<ManualMerge> merges, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(merges);
        try
        {
            await _kept.SaveMergesAsync(serverId, merges, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot keep the manual merges of a removed server for its watch state, so its copies stay in them: {Error}", exception.GetType().Name);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _kept.HasTitlesAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // Without the merges a merged copy would be missed and its record forgotten; the records wait for the next run.
            if (_merges.IsDamaged)
            {
                _logger.LogWarning("The manual merges cannot be read for now, so the kept watch state waits");
                return;
            }

            var users = _users.GetUsers().ToList();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var pruned = await _kept.PruneAsync(now - _keptFor, users.Select(user => user.Id).ToHashSet(), cancellationToken).ConfigureAwait(false);
            if (pruned > 0)
            {
                _logger.LogInformation("Forgot the kept watch state of {Count} titles nobody used", pruned);
            }

            var titles = await _kept.ReadAsync(cancellationToken).ConfigureAwait(false);
            var root = _store.Read(config => config.FederatedFolderPath);
            if (titles.Count == 0 || users.Count == 0 || root.Length == 0)
            {
                return;
            }

            var done = Restore(root, titles, users, cancellationToken);
            await _kept.ForgetAsync(done, cancellationToken).ConfigureAwait(false);
            if (done.Count > 0)
            {
                _logger.LogInformation("Put back the kept watch state of {Count} titles", done.Select(row => row.TitleId).Distinct().Count());
            }
        }
        catch (StoreSuspendedException)
        {
            // The database reports its own state; the records wait until it reads again.
            _logger.LogDebug("The kept watch state waits for the plugin database");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot put back the kept watch state: {Error}", exception.GetType().Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    // The servers of these titles: that of a server folder, or every server of a title of the flat layout.
    private static HashSet<Guid> ServersOf(IReadOnlyCollection<StubEntry> stubs, IReadOnlyCollection<SeriesStub> series)
        => stubs.SelectMany(stub => stub.ServerId is { } serverId ? [serverId] : stub.Sources.Select(source => source.ServerId))
            .Concat(series.SelectMany(entry => entry.ServerId is { } serverId ? [serverId] : entry.Sources.Select(source => source.ServerId)))
            .ToHashSet();

    private static bool HasAnything(UserItemData data) =>
        data.Played || data.PlayCount > 0 || data.PlaybackPositionTicks > 0 || data.LastPlayedDate is not null || data.IsFavorite || data.Rating is not null;

    private static KeptUserState StateOf(Guid userId, UserItemData data) =>
        new(userId, data.Played, data.PlayCount, data.PlaybackPositionTicks, data.LastPlayedDate, data.IsFavorite, data.Rating);

    private static UserItemData DataOf(KeptUserState state) => new()
    {
        Key = string.Empty,
        Played = state.Played,
        PlayCount = state.PlayCount,
        PlaybackPositionTicks = state.PlaybackPositionTicks,
        LastPlayedDate = state.LastPlayedUtc,
        IsFavorite = state.IsFavorite,
        Rating = state.Rating
    };

    // Matching reads only these two IDs on a film.
    private static bool HasNoFilmIds(ContentIds ids) => string.IsNullOrWhiteSpace(ids.Tmdb) && string.IsNullOrWhiteSpace(ids.Imdb);

    // A kept title as the dedup reads it; a series as an episode with no numbers, the form the work resolver groups.
    private static CatalogItem Saved(KeptTitle title) => new()
    {
        ServerId = SavedServer,
        ItemId = title.Id.ToString(CultureInfo.InvariantCulture),
        Kind = title.Kind == KeptTitleKind.Movie ? ContentKind.Movie : ContentKind.Episode,

        // Its own series, so that a merge names it like any copy.
        SeriesId = title.Kind == KeptTitleKind.Movie ? null : title.Id.ToString(CultureInfo.InvariantCulture),
        Title = title.Title,
        Year = title.Year,
        Ids = title.Ids,
        SeasonNumber = title.SeasonNumber,
        EpisodeNumber = title.EpisodeNumber,
        EpisodeNumberEnd = title.EpisodeNumberEnd,
        Metadata = title.Kind == KeptTitleKind.Movie
            ? new ComparableMetadata
            {
                OriginalTitle = title.OriginalTitle,
                RunTimeTicks = title.RunTimeTicks,
                Overview = title.Overview,
                Genres = title.Genres,
                Studios = title.Studios,
                People = new Lazy<IReadOnlyList<string>>(title.People)
            }
            : ComparableMetadata.None
    };

    // A kept title with the copies of its merges as one merge, so that each copy left finds it (D33).
    private static ManualMerge? MergeOf(KeptTitle title) => title.Copies.Count == 0 ? null : new ManualMerge
    {
        Id = Guid.ParseExact(title.Id.ToString("x32", CultureInfo.InvariantCulture), "N"),
        CreatedUtc = title.SavedUtc,
        Copies =
        [
            new MergedCopy
            {
                ServerId = SavedServer,
                ItemId = title.Id.ToString(CultureInfo.InvariantCulture),
                Kind = title.Kind == KeptTitleKind.Movie ? ContentKind.Movie : ContentKind.Episode
            },
            .. title.Copies
        ]
    };

    // The copies of every merge holding one of these items: a film, or a series by its series items. A merge kept for a
    // removed server counts only through a copy on that server.
    private static List<MergedCopy> CopiesOf(IEnumerable<StubSource> sources, IReadOnlyList<ManualMerge> merges, IReadOnlyList<(Guid ServerId, ManualMerge Merge)> kept)
    {
        var items = sources.Select(source => (source.ServerId.ToString("N"), source.ItemId)).ToHashSet();
        return merges
            .Where(merge => merge.Copies.Any(copy => items.Contains((copy.ServerId, copy.ItemId))))
            .Concat(kept
                .Where(entry => entry.Merge.Copies.Any(copy => copy.ServerId == entry.ServerId.ToString("N") && items.Contains((copy.ServerId, copy.ItemId))))
                .Select(entry => entry.Merge))
            .SelectMany(merge => merge.Copies)
            .DistinctBy(copy => (copy.ServerId, copy.ItemId, copy.Kind))
            .ToList();
    }

    private KeptTitle TitleOf(BaseItem item, Dictionary<Guid, BaseItem?> parents, DateTime now)
    {
        switch (item)
        {
            case Episode episode:
                if (!parents.TryGetValue(episode.SeriesId, out var parent))
                {
                    parent = _library.GetItemById(episode.SeriesId);
                    parents[episode.SeriesId] = parent;
                }

                return new KeptTitle
                {
                    OldItemId = item.Id,
                    Kind = KeptTitleKind.Episode,
                    Title = parent?.Name ?? episode.SeriesName,
                    Year = parent?.ProductionYear,
                    Ids = ContentIds.FromProviderIds(parent?.ProviderIds),
                    SeasonNumber = episode.ParentIndexNumber,
                    EpisodeNumber = episode.IndexNumber,
                    EpisodeNumberEnd = episode.IndexNumberEnd,
                    SavedUtc = now
                };
            case Series:
                return new KeptTitle
                {
                    OldItemId = item.Id,
                    Kind = KeptTitleKind.Series,
                    Title = item.Name,
                    Year = item.ProductionYear,
                    Ids = ContentIds.FromProviderIds(item.ProviderIds),
                    SavedUtc = now
                };
            default:
                var ids = ContentIds.FromProviderIds(item.ProviderIds);
                return new KeptTitle
                {
                    OldItemId = item.Id,
                    Kind = KeptTitleKind.Movie,
                    Title = item.Name,
                    Year = item.ProductionYear,
                    Ids = ids,
                    OriginalTitle = item.OriginalTitle,
                    RunTimeTicks = item.RunTimeTicks,
                    Overview = item.Overview,
                    Genres = item.Genres ?? [],
                    Studios = item.Studios ?? [],

                    // Only a film without IDs is matched by its people.
                    People = HasNoFilmIds(ids) ? _library.GetPeople(item).Select(person => person.Name).OfType<string>().ToList() : [],
                    SavedUtc = now
                };
        }
    }

    // The rows put back, by title and user. A user whose title has no item yet, or none the user sees, keeps the row.
    private List<(long TitleId, Guid UserId)> Restore(string root, IReadOnlyList<KeptTitle> titles, IReadOnlyList<User> users, CancellationToken cancellationToken)
    {
        var (current, currentSeries, paths) = CurrentStubs();
        var byId = titles.ToDictionary(title => title.Id.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var saved = titles.Where(title => title.Kind != KeptTitleKind.Series).Select(Saved).ToList();
        var savedSeries = titles.Where(title => title.Kind == KeptTitleKind.Series).Select(Saved).ToList();
        var done = new List<(long TitleId, Guid UserId)>();
        var pairs = DedupEngine.Pair(saved, savedSeries, current, currentSeries, [.. _merges.Merges, .. titles.Select(MergeOf).OfType<ManualMerge>()]);
        foreach (var match in pairs.GroupBy(pair => pair.Saved.ItemId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = byId[match.Key];
            var isSeries = title.Kind == KeptTitleKind.Series;
            var items = match
                .SelectMany(pair => paths[(isSeries, pair.Current.ServerId, pair.Current.ItemId)])
                .Distinct()
                .Select(path => _library.FindByPath(FederatedPaths.Resolve(root, path.Path), path.IsFolder))
                .OfType<BaseItem>()
                .Where(item => item.Id != title.OldItemId)
                .ToList();
            if (items.Count == 0)
            {
                continue;
            }

            var people = users.ToDictionary(user => user.Id);
            foreach (var state in title.Users)
            {
                if (people.TryGetValue(state.UserId, out var user) && PutBack(user, state, items, isSeries, cancellationToken))
                {
                    done.Add((title.Id, state.UserId));
                }
            }
        }

        return done;
    }

    // Each title gets the state once: on its primary version if the user sees it, otherwise on the first version the user
    // sees (decision 149). A series gets it on each copy the user sees.
    private bool PutBack(User user, KeptUserState state, IReadOnlyList<BaseItem> items, bool isSeries, CancellationToken cancellationToken)
    {
        var visible = items.Where(item => _library.GetItemById<BaseItem>(item.Id, user) is not null).ToList();
        var targets = isSeries
            ? visible
            : visible
                .GroupBy(item => item is Video { PrimaryVersionId: { } primary } ? primary : item.Id)
                .Select(title => _library.GetItemById<BaseItem>(title.Key, user) ?? title.First())
                .DistinctBy(item => item.Id)
                .ToList();
        if (targets.Count == 0)
        {
            return false;
        }

        try
        {
            foreach (var item in targets)
            {
                if (_userData.GetUserData(user, item) is not { } data)
                {
                    return false;
                }

                if (WatchStateMerge.Merge(DataOf(state), data))
                {
                    _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, cancellationToken);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path. The row stays for a later run.
            _logger.LogError("Cannot put back the kept watch state of a title: {Error}", exception.GetType().Name);
            return false;
        }
    }

    // Every indexed source of the stubs of configured servers, as the dedup reads it, with the stubs it is in. Sources keep
    // their server and item, so that a manual merge finds them (D21).
    private (List<CatalogItem> Titles, List<CatalogItem> Series, Dictionary<(bool IsSeries, string ServerId, string ItemId), List<(string Path, bool IsFolder)>> Paths) CurrentStubs()
    {
        var snapshot = _index.Snapshot;
        var configured = _servers.GetServers().Select(server => server.Id).ToHashSet();
        var indexed = snapshot.Servers
            .Where(catalog => configured.Contains(catalog.ServerId))
            .SelectMany(catalog => CatalogItemMapper.ToCatalogItems(catalog).Select(item => (catalog.ServerId, Item: item)))
            .GroupBy(entry => (entry.ServerId, entry.Item.ItemId))
            .ToDictionary(group => group.Key, group => group.First().Item);

        var state = _registry.State;
        var seriesPaths = state.Series.ToDictionary(entry => entry.Id, entry => entry.RelativePath, StringComparer.Ordinal);
        var items = new Dictionary<(bool IsSeries, string ServerId, string ItemId), CatalogItem>();
        var paths = new Dictionary<(bool IsSeries, string ServerId, string ItemId), List<(string Path, bool IsFolder)>>();
        void Add(CatalogItem item, bool isSeries, string path)
        {
            var key = (isSeries, item.ServerId, item.ItemId);
            items.TryAdd(key, item);
            if (!paths.TryGetValue(key, out var list))
            {
                paths[key] = list = [];
            }

            if (!list.Contains((path, isSeries)))
            {
                list.Add((path, isSeries));
            }
        }

        foreach (var stub in state.Stubs.Where(stub => stub.ServerId is { } server ? configured.Contains(server) : stub.Sources.Any(source => configured.Contains(source.ServerId))))
        {
            var sources = stub.Sources
                .Where(source => configured.Contains(source.ServerId))
                .Select(source => indexed.GetValueOrDefault((source.ServerId, source.ItemId)))
                .OfType<CatalogItem>();
            foreach (var item in sources)
            {
                Add(item, false, stub.RelativePath);
                if (item.Kind == ContentKind.Episode && stub.SeriesStubId is { } seriesId && seriesPaths.TryGetValue(seriesId, out var seriesPath))
                {
                    // A series is its series item, or the episode itself when that is not known.
                    Add(item with { ItemId = item.SeriesId ?? item.ItemId, SeasonNumber = null, EpisodeNumber = null, EpisodeNumberEnd = null }, true, seriesPath);
                }
            }
        }

        return (
            items.Where(entry => !entry.Key.IsSeries).Select(entry => entry.Value).ToList(),
            items.Where(entry => entry.Key.IsSeries).Select(entry => entry.Value).ToList(),
            paths);
    }

    // Saves the state of the titles, or says why not in the log; false when they must stay.
    private async Task<bool> SaveAsync(string root, IReadOnlyCollection<StubEntry> stubs, IReadOnlyCollection<SeriesStub> series, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // No users may mean they cannot be read for now (decision 165), and the stubs would go with their state.
            var users = _users.GetUsers().ToList();
            if (users.Count == 0)
            {
                _logger.LogWarning("The users cannot be read for now: {Count} titles of removed servers are kept until their watch state is saved", stubs.Count + series.Count);
                return false;
            }

            // Without the merges a kept title would miss the copies that find it (D33): the titles stay until they read.
            if (_merges.IsDamaged)
            {
                _logger.LogWarning("The manual merges cannot be read for now: {Count} titles of removed servers are kept until their watch state is saved", stubs.Count + series.Count);
                return false;
            }

            var merges = _merges.Merges;
            var keptMerges = await _kept.ReadMergesAsync(cancellationToken).ConfigureAwait(false);
            var seriesById = series.Concat(_registry.State.Series).DistinctBy(entry => entry.Id).ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var parents = new Dictionary<Guid, BaseItem?>();
            var titles = new List<KeptTitle>();
            var entries = stubs
                .Select(stub => (Path: stub.RelativePath, IsFolder: false, Sources: stub.SeriesStubId is { } seriesId && seriesById.TryGetValue(seriesId, out var parent) ? parent.Sources : stub.Sources))
                .Concat(series.Select(entry => (Path: entry.RelativePath, IsFolder: true, entry.Sources)));
            foreach (var path in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_library.FindByPath(FederatedPaths.Resolve(root, path.Path), path.IsFolder) is not { } item)
                {
                    continue;
                }

                var rows = users
                    .Select(user => (user.Id, Data: _userData.GetUserData(user, item)))
                    .Where(row => row.Data is not null && HasAnything(row.Data))
                    .Select(row => StateOf(row.Id, row.Data!))
                    .ToList();
                if (rows.Count > 0)
                {
                    titles.Add(TitleOf(item, parents, now) with { Users = rows, Copies = CopiesOf(path.Sources, merges, keptMerges) });
                }
            }

            await _kept.SaveAsync(titles, cancellationToken).ConfigureAwait(false);
            if (titles.Count > 0)
            {
                _logger.LogInformation("Kept the watch state of {Count} titles of removed servers", titles.Count);
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot keep the watch state of the titles of removed servers, so they stay for now: {Error}", exception.GetType().Name);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
