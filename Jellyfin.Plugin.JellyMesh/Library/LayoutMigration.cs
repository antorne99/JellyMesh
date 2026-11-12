using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILayoutMigration"/> through the watch state carrier. An old stub goes to the new stubs that share its
/// remote items or its identity, with their version groups and the local primary; the old stubs stay out of the groups
/// and play as before until the emptying (D12). Progress is read from the registry and the libraries, so a restart resumes.
/// </summary>
public sealed class LayoutMigration : ILayoutMigration
{
    private readonly StubRegistry _registry;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IRemoteServerRegistry _servers;
    private readonly IFederationIndex _index;
    private readonly IWatchStateCarrier _carrier;
    private readonly StubRemover _remover;
    private readonly ILogger<LayoutMigration> _logger;
    private volatile LastRun? _lastRun;

    /// <summary>
    /// Initializes a new instance of the <see cref="LayoutMigration"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="store">The configuration, for the federated folder.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="servers">The configured servers, whose folders the libraries need.</param>
    /// <param name="index">The index, which tells a title gone from every server.</param>
    /// <param name="carrier">The watch state carrier.</param>
    /// <param name="remover">The stub remover, which empties the old folders.</param>
    /// <param name="logger">The logger.</param>
    public LayoutMigration(
        StubRegistry registry,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IRemoteServerRegistry servers,
        IFederationIndex index,
        IWatchStateCarrier carrier,
        StubRemover remover,
        ILogger<LayoutMigration> logger)
    {
        _registry = registry;
        _store = store;
        _library = library;
        _servers = servers;
        _index = index;
        _carrier = carrier;
        _remover = remover;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath) ?? string.Empty;
        if (root.Length == 0 || !HasOldTitles(_registry.State))
        {
            return;
        }

        // Until every library on an old path holds the new ones, and each server folder is in a library, the old layout stays (Q49, Q215).
        if (LibrariesOf(root) is not { } libraries || !libraries.ServerFoldersLinked || libraries.OnOldPaths.Any(library => library.MissingPaths.Count > 0))
        {
            return;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _registry.State;
            var snapshot = _index.Snapshot;
            if (!HasOldTitles(state) || snapshot.Health == IndexHealth.Corrupt)
            {
                return;
            }

            var catalog = new Catalog(IndexItemLookup.Build(snapshot), _servers.GetServers().Select(server => server.Id).ToHashSet(), state.RemovedServers.ToHashSet());
            var moves = new List<WatchStateMove>();
            var waiting = 0;
            var blocked = 0;
            foreach (var stub in state.Stubs.Where(stub => stub.ServerId is null))
            {
                Add(TitleMoveOf(root, state, stub, catalog), moves, ref waiting);
            }

            foreach (var series in state.Series.Where(series => series.ServerId is null))
            {
                Add(SeriesMoveOf(root, state, series, catalog), moves, ref waiting);
            }

            if (moves.Count > 0)
            {
                var report = await _carrier.CarryAsync(moves, cancellationToken).ConfigureAwait(false);
                blocked = report.Moves.Count(IsBlocked);
                waiting += report.Moves.Count(outcome => !IsDone(outcome) && !IsBlocked(outcome));
                var refused = report.Moves.Count(outcome => outcome.Status == WatchStateMoveStatus.Refused);
                if (refused > 0)
                {
                    _logger.LogWarning("Migration to the server folders: the watch state carry refused {Refused} old titles, which go without it", refused);
                }
            }

            _lastRun = new LastRun(waiting, blocked);
            if (waiting + blocked > 0)
            {
                _logger.LogInformation("Migration to the server folders: {Waiting} old titles wait for a scan and {Blocked} are blocked", waiting, blocked);
                return;
            }

            await EmptyAsync(root, state).ConfigureAwait(false);
        }
        finally
        {
            _registry.Exit();
        }
    }

    /// <inheritdoc />
    public LayoutMigrationStatus? GetStatus(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Length == 0)
        {
            return null;
        }

        var state = _registry.State;
        var old = state.Stubs.Count(stub => stub.ServerId is null) + state.Series.Count(series => series.ServerId is null);
        var read = LibrariesOf(root);
        var libraries = read?.OnOldPaths ?? [];
        if (old == 0 && libraries.Count == 0)
        {
            return null;
        }

        var step = old == 0
            ? LayoutMigrationStep.RemoveOldPaths
            : read is { ServerFoldersLinked: false } || libraries.Any(library => library.MissingPaths.Count > 0) ? LayoutMigrationStep.AddPaths : LayoutMigrationStep.Carrying;
        return new LayoutMigrationStatus(step, libraries, old) { Waiting = old == 0 ? null : _lastRun?.Waiting, Blocked = old == 0 ? null : _lastRun?.Blocked };
    }

    private static bool HasOldTitles(StubState state) =>
        state.Stubs.Any(stub => stub.ServerId is null) || state.Series.Any(series => series.ServerId is null);

    // Done for every user, left only for users who see none of the new items (decision 166), or refused as not valid (Q216).
    private static bool IsDone(WatchStateMoveOutcome outcome) =>
        outcome.Status is WatchStateMoveStatus.Carried or WatchStateMoveStatus.Refused
        || (outcome.Status == WatchStateMoveStatus.UsersLeft && outcome.SkippedUsers.All(user => user.Reason == WatchStateUserSkipReason.NoVisibleVersion));

    // Not done for a reason a scan does not fix: a failed move, or a user row missing or not saved (Q216).
    private static bool IsBlocked(WatchStateMoveOutcome outcome) =>
        outcome.Status == WatchStateMoveStatus.Failed
        || (outcome.Status == WatchStateMoveStatus.UsersLeft && !IsDone(outcome));

    private static void Add((WatchStateMove? Move, bool Waits) step, List<WatchStateMove> moves, ref int waiting)
    {
        if (step.Move is { } move)
        {
            moves.Add(move);
        }
        else if (step.Waits)
        {
            waiting++;
        }
    }

    // Gone as the stub planner decides (decision 44): sources only on unconfigured servers go if the admin removed them all,
    // otherwise each configured server must be read in full and lack the item, since a lost index proves nothing.
    private static bool IsGone(IReadOnlyList<StubSource> sources, Catalog catalog)
    {
        if (sources.Count > 0 && sources.All(source => !catalog.Configured.Contains(source.ServerId)))
        {
            return sources.All(source => catalog.Removed.Contains(source.ServerId));
        }

        var snapshot = catalog.Items.Snapshot;
        return snapshot.Health == IndexHealth.Ok && sources.Where(source => catalog.Configured.Contains(source.ServerId)).All(source =>
            snapshot.IsAuthoritativeFor(source.ServerId) && catalog.Items.Find(source.ServerId, source.ItemId) is null);
    }

    private static HashSet<(Guid ServerId, string ItemId)> ItemsOf(IReadOnlyList<StubSource> sources) =>
        sources.Select(source => (source.ServerId, source.ItemId)).ToHashSet();

    // The stubs of the server folders with a remote item or the identity of the old stub.
    private static List<StubEntry> NewStubsOf(StubState state, StubEntry old)
    {
        var items = ItemsOf(old.Sources);
        return state.Stubs
            .Where(stub => stub.ServerId is not null && stub.Kind == old.Kind
                && (string.Equals(stub.IdentityKey, old.IdentityKey, StringComparison.Ordinal) || stub.Sources.Any(source => items.Contains((source.ServerId, source.ItemId)))))
            .ToList();
    }

    // The series of the server folders with a remote item or the work of the old series, or holding one of its new episodes.
    private static List<SeriesStub> NewSeriesOf(StubState state, SeriesStub old)
    {
        var items = ItemsOf(old.Sources);
        var episodes = state.Stubs
            .Where(stub => stub.ServerId is null && string.Equals(stub.SeriesStubId, old.Id, StringComparison.Ordinal))
            .SelectMany(stub => NewStubsOf(state, stub))
            .Select(stub => stub.SeriesStubId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return state.Series
            .Where(series => series.ServerId is not null
                && (string.Equals(series.WorkKey, old.WorkKey, StringComparison.Ordinal)
                    || series.Sources.Any(source => items.Contains((source.ServerId, source.ItemId)))
                    || episodes.Contains(series.Id)))
            .ToList();
    }

    // The move of an old film or episode, or whether it still waits: an old stub with no item, or whose title is gone, has
    // nothing to carry.
    private (WatchStateMove? Move, bool Waits) TitleMoveOf(string root, StubState state, StubEntry old, Catalog catalog)
    {
        if (_library.FindByPath(FederatedPaths.Resolve(root, old.RelativePath), false) is not Video item)
        {
            return (null, false);
        }

        var news = NewStubsOf(state, old);
        if (news.Count == 0)
        {
            return (null, !IsGone(old.Sources, catalog));
        }

        return TargetsOf(root, news) is { } targets ? (WatchStateMove.Of(old, item.Id, targets), false) : (null, true);
    }

    private (WatchStateMove? Move, bool Waits) SeriesMoveOf(string root, StubState state, SeriesStub old, Catalog catalog)
    {
        if (_library.FindByPath(FederatedPaths.Resolve(root, old.RelativePath), true) is not Series item)
        {
            return (null, false);
        }

        var news = NewSeriesOf(state, old);
        if (news.Count == 0)
        {
            return (null, !IsGone(old.Sources, catalog));
        }

        var targets = new List<WatchStateTarget>();
        foreach (var series in news)
        {
            if (_library.FindByPath(FederatedPaths.Resolve(root, series.RelativePath), true) is not Series found)
            {
                return (null, true);
            }

            targets.Add(new WatchStateTarget(found.Id, series.ServerId));
        }

        return (WatchStateMove.OfSeries(old, item.Id, targets), false);
    }

    // The new versions of the title: the stubs with their groups, and the local copy that is the primary of its group.
    // Null while one is not scanned yet, or a group with a local copy is not linked yet.
    private List<WatchStateTarget>? TargetsOf(string root, List<StubEntry> news)
    {
        var groups = news.Select(stub => _registry.FindGroup(stub.Id)).OfType<VersionGroup>().DistinctBy(group => group.Id).ToList();
        var stubs = news
            .Concat(groups.SelectMany(group => group.StubIds).Select(_registry.Find).OfType<StubEntry>())
            .DistinctBy(stub => stub.Id);
        var targets = new List<WatchStateTarget>();
        var primaries = new HashSet<Guid>();
        foreach (var stub in stubs)
        {
            if (_library.FindByPath(FederatedPaths.Resolve(root, stub.RelativePath), false) is not Video item)
            {
                return null;
            }

            targets.Add(new WatchStateTarget(item.Id, stub.ServerId));
            if (item.PrimaryVersionId is { } primary)
            {
                primaries.Add(primary);
            }
        }

        foreach (var group in groups.Where(group => group.LocalItemIds.Count > 0))
        {
            // The local version is the primary of the group: a local copy, or the film the admin merged it into (Q43, Q220).
            var head = LocalVersionHead.Of(group.LocalItemIds, _library, item => FederatedPaths.IsInside(root, item.Path));
            var local = group.LocalItemIds.Append(head?.Id ?? Guid.Empty).FirstOrDefault(primaries.Contains);
            if (local == Guid.Empty || _library.GetItemById(local) is not Video)
            {
                return null;
            }

            targets.Add(new WatchStateTarget(local, null));
        }

        return targets;
    }

    // Removes every old stub and series, with their folders and items; what fails stays for the next run.
    private async Task EmptyAsync(string root, StubState state)
    {
        var stubs = _remover.RemoveAll(root, state, state.Stubs.Where(stub => stub.ServerId is null).ToList());
        var series = _remover.RemoveSeries(root, stubs.State, stubs.State.Series.Where(entry => entry.ServerId is null).ToList());

        // The files are gone by now, so the registry must say so even if the run is being cancelled.
        await _registry.SaveAsync(series.State, CancellationToken.None).ConfigureAwait(false);
        var failures = stubs.Failures.Count + series.Failures.Count;
        if (failures > 0)
        {
            _logger.LogWarning("Migration to the server folders: {Failures} old stubs or series could not be removed, the next run tries again", failures);
        }
        else
        {
            _logger.LogInformation("Migration to the server folders: the watch state is carried and the old folders are empty");
        }
    }

    // The libraries on an old path, with the server folders of the same kind they lack, and whether every server folder is
    // in some library; null when they cannot be read.
    private Libraries? LibrariesOf(string root)
    {
        List<VirtualFolderInfo> folders;
        try
        {
            folders = _library.GetVirtualFolders();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot read the libraries for the migration to the server folders: {Error}", exception.GetType().Name);
            return null;
        }

        var servers = _servers.GetServers();
        var kinds = new (string Old, Func<Guid, string> New)[]
        {
            (FederatedFolderLayout.MoviesPath(root), server => FederatedFolderLayout.ServerMoviesPath(root, server)),
            (FederatedFolderLayout.SeriesPath(root), server => FederatedFolderLayout.ServerSeriesPath(root, server))
        };
        var libraries = new List<LayoutMigrationLibrary>();
        var linked = servers
            .SelectMany(server => kinds.Select(kind => kind.New(server.Id)))
            .All(path => folders.Any(folder => (folder.Locations ?? []).Any(location => FederatedFolderLinkChecker.IsSamePath(location, path))));
        foreach (var folder in folders)
        {
            var locations = folder.Locations ?? [];
            bool Holds(string path) => locations.Any(location => FederatedFolderLinkChecker.IsSamePath(location, path));

            var old = kinds.Where(kind => Holds(kind.Old)).ToList();
            if (old.Count > 0)
            {
                var missing = old.SelectMany(kind => servers.Select(server => kind.New(server.Id))).Where(path => !Holds(path)).ToList();
                libraries.Add(new LayoutMigrationLibrary(folder.Name, old.Select(kind => kind.Old).ToList(), missing));
            }
        }

        return new Libraries(libraries, linked);
    }

    private sealed record Libraries(List<LayoutMigrationLibrary> OnOldPaths, bool ServerFoldersLinked);

    private sealed record LastRun(int Waiting, int Blocked);

    // The index with the configured servers and the ones the admin removed, read once per run.
    private sealed record Catalog(IndexItemLookup Items, HashSet<Guid> Configured, HashSet<Guid> Removed);
}
