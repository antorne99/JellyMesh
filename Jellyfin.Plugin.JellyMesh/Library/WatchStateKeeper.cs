using System;
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
    private const string CurrentServer = "current";

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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WatchStateKeeper> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
        _timeProvider = timeProvider;
        _logger = logger;
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

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var parents = new Dictionary<Guid, BaseItem?>();
            var titles = new List<KeptTitle>();
            foreach (var path in stubs.Select(stub => (Path: stub.RelativePath, IsFolder: false)).Concat(series.Select(entry => (Path: entry.RelativePath, IsFolder: true))))
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
                    titles.Add(TitleOf(item, parents, now) with { Users = rows });
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

    /// <inheritdoc />
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (!_kept.Exists)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        var pairs = DedupEngine.Pair(saved, savedSeries, current, currentSeries);
        foreach (var match in pairs.GroupBy(pair => pair.Saved.ItemId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = byId[match.Key];
            var items = match
                .Select(pair => paths[pair.Current.ItemId])
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
                if (people.TryGetValue(state.UserId, out var user) && PutBack(user, state, items, title.Kind == KeptTitleKind.Series, cancellationToken))
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

    // The stubs of configured servers as the dedup reads them, by stub id, with where each one is.
    private (List<CatalogItem> Titles, List<CatalogItem> Series, Dictionary<string, (string Path, bool IsFolder)> Paths) CurrentStubs()
    {
        var snapshot = _index.Snapshot;
        var configured = _servers.GetServers().Select(server => server.Id).ToHashSet();
        var indexed = snapshot.Servers
            .Where(catalog => configured.Contains(catalog.ServerId))
            .SelectMany(catalog => CatalogItemMapper.ToCatalogItems(catalog).Select(item => (catalog.ServerId, Item: item)))
            .GroupBy(entry => (entry.ServerId, entry.Item.ItemId))
            .ToDictionary(group => group.Key, group => group.First().Item);

        var state = _registry.State;
        var titles = new List<CatalogItem>();
        var series = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        var paths = new Dictionary<string, (string Path, bool IsFolder)>(StringComparer.Ordinal);
        foreach (var stub in state.Stubs.Where(stub => stub.ServerId is { } server ? configured.Contains(server) : stub.Sources.Any(source => configured.Contains(source.ServerId))))
        {
            var item = stub.Sources
                .Where(source => configured.Contains(source.ServerId))
                .Select(source => indexed.GetValueOrDefault((source.ServerId, source.ItemId)))
                .FirstOrDefault(found => found is not null);
            if (item is null)
            {
                continue;
            }

            titles.Add(item with { ServerId = CurrentServer, ItemId = stub.Id });
            paths[stub.Id] = (stub.RelativePath, false);
            if (item.Kind == ContentKind.Episode && stub.SeriesStubId is { } seriesId && !series.ContainsKey(seriesId))
            {
                series[seriesId] = item with { ServerId = CurrentServer, ItemId = seriesId, SeasonNumber = null, EpisodeNumber = null, EpisodeNumberEnd = null };
            }
        }

        foreach (var entry in state.Series.Where(entry => series.ContainsKey(entry.Id)))
        {
            paths[entry.Id] = (entry.RelativePath, true);
        }

        return (titles, series.Values.Where(item => paths.ContainsKey(item.ItemId)).ToList(), paths);
    }
}
