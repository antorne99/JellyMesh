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
public sealed partial class LayoutMigration : ILayoutMigration, IDisposable
{
    private readonly StubRegistry _registry;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IRemoteServerRegistry _servers;
    private readonly IFederationIndex _index;
    private readonly IWatchStateCarrier _carrier;
    private readonly StubRemover _remover;
    private readonly IVersionLinker _linker;
    private readonly ILibraryPaths _paths;
    private readonly ILibraryScans _scans;
    private readonly ILibraryMaterializer _materializer;
    private readonly IServerLibraryManager _automaticLibraries;
    private readonly ILogger<LayoutMigration> _logger;
    private readonly IWatchStateKeeper? _keeper;
    private readonly IMigrationSync? _sync;
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
    /// <param name="linker">The version linker, which tells whether its last run failed.</param>
    /// <param name="paths">The service that adds and removes the federated folders in the libraries.</param>
    /// <param name="scans">The library scans the automatic migration waits for.</param>
    /// <param name="materializer">The materializer, which writes the server folders before they are added.</param>
    /// <param name="automaticLibraries">The manager of the automatic libraries, which follows them before placement.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="keeper">What keeps the watch state of the old titles of removed servers before they go (row 412).</param>
    /// <param name="sync">The sync the automatic migration runs before its scan (§10 row 420).</param>
    public LayoutMigration(
        StubRegistry registry,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IRemoteServerRegistry servers,
        IFederationIndex index,
        IWatchStateCarrier carrier,
        StubRemover remover,
        IVersionLinker linker,
        ILibraryPaths paths,
        ILibraryScans scans,
        ILibraryMaterializer materializer,
        IServerLibraryManager automaticLibraries,
        ILogger<LayoutMigration> logger,
        IWatchStateKeeper? keeper = null,
        IMigrationSync? sync = null)
    {
        _registry = registry;
        _store = store;
        _library = library;
        _servers = servers;
        _index = index;
        _carrier = carrier;
        _remover = remover;
        _linker = linker;
        _paths = paths;
        _scans = scans;
        _materializer = materializer;
        _automaticLibraries = automaticLibraries;
        _logger = logger;
        _keeper = keeper;
        _sync = sync;
    }

    /// <inheritdoc />
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var (root, migration) = _store.Read(config => (config.FederatedFolderPath ?? string.Empty, Copy(config.LayoutMigration)));
        // Nothing moves before the admin chooses in the banner (Q55, §10 row 217).
        if (migration.Mode == LayoutMigrationMode.None || root.Length == 0)
        {
            return;
        }

        if (migration.Mode == LayoutMigrationMode.Automatic)
        {
            await ContinueAsync(root, migration, cancellationToken).ConfigureAwait(false);
            return;
        }

        // With no server configured every server folder looks linked, so nothing starts (Q260).
        if (!HasOldTitles(_registry.State) || _servers.GetServers().Count == 0)
        {
            return;
        }

        // Until every library on an old path holds the new ones, and each server folder is in a library, the old layout
        // stays; only the kinds with old titles count (Q49, Q215, Q249).
        if (LibrariesOf(root, _registry.State) is not { } libraries || !libraries.ServerFoldersLinked || libraries.OnOldPaths.Any(library => library.MissingPaths.Count > 0))
        {
            return;
        }

        await MigrateLockedAsync(root, false, cancellationToken).ConfigureAwait(false);
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
        var old = state.Stubs.Count(stub => IsOld(stub.RelativePath)) + state.Series.Count(series => IsOld(series.RelativePath));
        var read = LibrariesOf(root, state);
        var libraries = read?.OnOldPaths ?? [];
        var migration = _store.Read(config => Copy(config.LayoutMigration));
        var automatic = migration.Mode == LayoutMigrationMode.Automatic;
        if (old == 0 && libraries.Count == 0 && !(automatic && migration.Error != AutomaticMigrationError.None))
        {
            return null;
        }

        var step = old == 0
            ? LayoutMigrationStep.RemoveOldPaths
            : read is { ServerFoldersLinked: false } || libraries.Any(library => library.MissingPaths.Count > 0) ? LayoutMigrationStep.AddPaths : LayoutMigrationStep.Carrying;
        return new LayoutMigrationStatus(step, libraries, old)
        {
            Waiting = old == 0 ? null : _lastRun?.Waiting,
            Blocked = old == 0 ? null : _lastRun?.Blocked,
            Held = old == 0 ? null : _lastRun?.Held,
            UnlinkedFolders = read?.Unlinked ?? [],
            Mode = migration.Mode,
            AutomaticStep = automatic ? migration.Step : null,
            Running = IsRunning,
            Error = automatic ? migration.Error : AutomaticMigrationError.None,
            ErrorLibrary = automatic && migration.ErrorLibrary.Length > 0 ? migration.ErrorLibrary : null
        };
    }

    private static bool HasOldTitles(StubState state) =>
        state.Stubs.Any(stub => IsOld(stub.RelativePath)) || state.Series.Any(series => IsOld(series.RelativePath));

    // Whether old titles of one type are left.
    private static bool HasOldTitlesOf(StubState state, TypeFolder type) => type == TypeFolder.Movies
        ? state.Stubs.Any(stub => stub.Kind == StubKind.Movie && IsOld(stub.RelativePath))
        : state.Series.Any(series => IsOld(series.RelativePath)) || state.Stubs.Any(stub => stub.Kind == StubKind.Episode && IsOld(stub.RelativePath));

    // A stub or series of the flat layout or of the 0.5 server folders, which the migration carries to the remote library layout.
    private static bool IsOld(string relativePath) => FederatedPaths.RemoteLibraryOf(relativePath) is null;

    // Done for every user, left only for users who see none of the new items (decision 166), or refused for good (Q216, Q259).
    private static bool IsDone(WatchStateMoveOutcome outcome, bool unsettled) =>
        outcome.Status == WatchStateMoveStatus.Carried || IsFinalRefusal(outcome, unsettled) || LeftOnlyUnseeing(outcome);

    // A refusal a later sync cannot lift: none for a missing federated folder, nor while the folder changed or the link failed (Q259).
    private static bool IsFinalRefusal(WatchStateMoveOutcome outcome, bool unsettled) =>
        outcome.Status == WatchStateMoveStatus.Refused && !unsettled && outcome.Refusal != WatchStateRefusal.NoFederatedFolder;

    private static bool LeftOnlyUnseeing(WatchStateMoveOutcome outcome) =>
        outcome.Status == WatchStateMoveStatus.UsersLeft && outcome.SkippedUsers.All(user => user.Reason == WatchStateUserSkipReason.NoVisibleVersion);

    // Not done for a reason a scan does not fix: a failed move, or a user row missing or not saved (Q216).
    private static bool IsBlocked(WatchStateMoveOutcome outcome) =>
        outcome.Status == WatchStateMoveStatus.Failed
        || (outcome.Status == WatchStateMoveStatus.UsersLeft && !LeftOnlyUnseeing(outcome));

    private static void Add(Step step, List<WatchStateMove> moves, Dictionary<(Guid ServerId, string LibraryId), int> held, ref int waiting, ref int heldTitles)
    {
        if (step.Move is { } move)
        {
            moves.Add(move);
        }
        else if (step.Held.Count > 0)
        {
            heldTitles++;
            foreach (var library in step.Held)
            {
                held[library] = held.GetValueOrDefault(library) + 1;
            }
        }
        else if (step.Waits)
        {
            waiting++;
        }
    }

    // Gone as the stub planner decides (decision 44): sources only on unconfigured servers go if the admin removed them all,
    // otherwise each configured server must be read in full and lack the item. Held when only empty imported libraries keep it (Q270).
    private static Step FateOf(IReadOnlyList<StubSource> sources, Catalog catalog)
    {
        if (sources.Count > 0 && sources.All(source => !catalog.Configured.ContainsKey(source.ServerId)))
        {
            return sources.All(source => catalog.Removed.Contains(source.ServerId)) ? Step.None : Step.Waiting;
        }

        if (EmptyLibraryHold.Of(catalog.Items.Snapshot, catalog.Configured, sources) is not { } hold)
        {
            return Step.Waiting;
        }

        var missing = sources
            .Where(source => catalog.Configured.ContainsKey(source.ServerId) && !(source.LibraryId is { } library && hold.Contains((source.ServerId, library))))
            .All(source => catalog.Items.Find(source.ServerId, source.ItemId) is null);
        return !missing ? Step.Waiting : hold.Count > 0 ? new Step(null, false, hold) : Step.None;
    }

    // The old stubs and series whose sources are all on servers the admin removed, as FateOf tells them (decision 44).
    private static (List<StubEntry> Stubs, List<SeriesStub> Series) OnlyOnRemoved(StubState state, Catalog catalog)
    {
        bool Removed(IReadOnlyList<StubSource> sources) =>
            sources.Count > 0 && sources.All(source => !catalog.Configured.ContainsKey(source.ServerId) && catalog.Removed.Contains(source.ServerId));

        return (
            state.Stubs.Where(stub => IsOld(stub.RelativePath) && Removed(stub.Sources)).ToList(),
            state.Series.Where(series => IsOld(series.RelativePath) && Removed(series.Sources)).ToList());
    }

    private static HashSet<(Guid ServerId, string ItemId)> ItemsOf(IReadOnlyList<StubSource> sources) =>
        sources.Select(source => (source.ServerId, source.ItemId)).ToHashSet();

    // The stubs of the remote library layout with a remote item or the identity of the old stub.
    private static List<StubEntry> NewStubsOf(StubState state, StubEntry old)
    {
        var items = ItemsOf(old.Sources);
        return state.Stubs
            .Where(stub => stub.ServerId is not null && !IsOld(stub.RelativePath) && stub.Kind == old.Kind
                && (string.Equals(stub.IdentityKey, old.IdentityKey, StringComparison.Ordinal) || stub.Sources.Any(source => items.Contains((source.ServerId, source.ItemId)))))
            .ToList();
    }

    // The series of the remote library layout with a remote item or the work of the old series, or holding one of its new episodes.
    private static List<SeriesStub> NewSeriesOf(StubState state, SeriesStub old)
    {
        var items = ItemsOf(old.Sources);
        var episodes = state.Stubs
            .Where(stub => IsOld(stub.RelativePath) && string.Equals(stub.SeriesStubId, old.Id, StringComparison.Ordinal))
            .SelectMany(stub => NewStubsOf(state, stub))
            .Select(stub => stub.SeriesStubId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return state.Series
            .Where(series => series.ServerId is not null && !IsOld(series.RelativePath)
                && (string.Equals(series.WorkKey, old.WorkKey, StringComparison.Ordinal)
                    || series.Sources.Any(source => items.Contains((source.ServerId, source.ItemId)))
                    || episodes.Contains(series.Id)))
            .ToList();
    }

    // Carries and empties under the registry lock, then logs the empty libraries holding old titles.
    private async Task MigrateLockedAsync(string root, bool automatic, CancellationToken cancellationToken)
    {
        Dictionary<(Guid ServerId, string LibraryId), int> held;
        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            held = await MigrateAsync(root, automatic, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registry.Exit();
        }

        await LogHeldAsync(held, cancellationToken).ConfigureAwait(false);
    }

    // Carries the old titles and empties the old folders once nothing waits; returns the empty libraries holding titles.
    // Runs under the registry lock. The automatic migration tells the carrier the old items go (decisions 179, 187).
    private async Task<Dictionary<(Guid ServerId, string LibraryId), int>> MigrateAsync(string root, bool automatic, CancellationToken cancellationToken)
    {
        var held = new Dictionary<(Guid ServerId, string LibraryId), int>();
        var state = _registry.State;
        var snapshot = _index.Snapshot;
        if (!HasOldTitles(state) || snapshot.Health == IndexHealth.Corrupt)
        {
            return held;
        }

        // Read again under the lock, which a removal from the page takes (decision 44).
        var configured = _servers.GetServers().DistinctBy(server => server.Id).ToDictionary(server => server.Id);
        if (configured.Count == 0)
        {
            return held;
        }

        var catalog = new Catalog(IndexItemLookup.Build(snapshot), configured, state.RemovedServers.ToHashSet());
        var moves = new List<WatchStateMove>();
        var waiting = 0;
        var blocked = 0;
        var heldTitles = 0;
        foreach (var stub in state.Stubs.Where(stub => IsOld(stub.RelativePath)))
        {
            Add(TitleMoveOf(root, state, stub, catalog, automatic), moves, held, ref waiting, ref heldTitles);
        }

        foreach (var series in state.Series.Where(series => IsOld(series.RelativePath)))
        {
            Add(SeriesMoveOf(root, state, series, catalog, automatic), moves, held, ref waiting, ref heldTitles);
        }

        var folderChanged = false;
        IReadOnlyList<Guid> forgettable = [];
        if (moves.Count > 0)
        {
            var report = await _carrier.CarryAsync(moves, cancellationToken).ConfigureAwait(false);
            forgettable = report.ForgettableOldItemIds;

            // A refusal is for good only if the carry read this folder and the link of the round did not fail (Q259).
            folderChanged = report.Root is not null && !string.Equals(report.Root, root, StringComparison.Ordinal);
            var unsettled = folderChanged || _linker.LastRunFailed;
            blocked = report.Moves.Count(IsBlocked);
            waiting += report.Moves.Count(outcome => !IsDone(outcome, unsettled) && !IsBlocked(outcome));
            var refused = report.Moves.Count(outcome => IsFinalRefusal(outcome, unsettled));
            if (refused > 0)
            {
                _logger.LogWarning("Migration to the server folders: the watch state carry refused {Refused} old titles, which go without it", refused);
            }

            var later = report.Moves.Count(outcome => outcome.Status == WatchStateMoveStatus.Refused) - refused;
            if (later > 0)
            {
                _logger.LogInformation("Migration to the server folders: the watch state carry refused {Refused} old titles for now, tried again at the next sync", later);
            }
        }

        // The old folders of a federated folder changed under the run are not the ones to empty.
        folderChanged |= !string.Equals(_store.Read(config => config.FederatedFolderPath) ?? string.Empty, root, StringComparison.Ordinal);
        _lastRun = new LastRun(waiting, blocked, heldTitles);
        if (waiting + blocked + heldTitles > 0 || folderChanged)
        {
            _logger.LogInformation(
                "Migration to the server folders: {Waiting} old titles wait for a scan, {Held} for an empty remote library and {Blocked} are blocked",
                waiting,
                heldTitles,
                blocked);
            return held;
        }

        // The old titles of removed servers go with no carry, so their watch state is kept first; while it cannot be, they wait.
        var (removedStubs, removedSeries) = OnlyOnRemoved(state, catalog);
        if (_keeper is not null && !await _keeper.KeepAsync(root, removedStubs, removedSeries, cancellationToken).ConfigureAwait(false))
        {
            _lastRun = new LastRun(removedStubs.Count + removedSeries.Count, 0, 0);
            _logger.LogInformation("Migration to the server folders: {Waiting} old titles of removed servers wait until their watch state is kept", removedStubs.Count + removedSeries.Count);
            return held;
        }

        await EmptyAsync(root, state).ConfigureAwait(false);

        // Only after the old items are gone: the carrier keeps the records of any still in the library (decisions 187, 204).
        if (forgettable.Count > 0)
        {
            await _carrier.ForgetRemovedAsync(forgettable, cancellationToken).ConfigureAwait(false);
        }

        return held;
    }

    // Logs each empty library holding old titles by its remote name, or by its id when the server cannot list them.
    private async Task LogHeldAsync(Dictionary<(Guid ServerId, string LibraryId), int> held, CancellationToken cancellationToken)
    {
        var servers = _servers.GetServers().DistinctBy(server => server.Id).ToDictionary(server => server.Id, server => server.Name);
        foreach (var server in held.GroupBy(entry => entry.Key.ServerId))
        {
            var read = await _servers.GetLibrariesAsync(server.Key, cancellationToken).ConfigureAwait(false);
            var names = read.IsSuccess ? read.Value!.DistinctBy(library => library.Id).ToDictionary(library => library.Id, library => library.Name, StringComparer.Ordinal) : [];
            foreach (var library in server)
            {
                _logger.LogWarning(
                    "Migration to the server folders: {Count} old titles wait while the library {Library} of remote server {ServerName} came back empty",
                    library.Value,
                    LogText.Clean(names.GetValueOrDefault(library.Key.LibraryId, library.Key.LibraryId)),
                    LogText.Clean(servers.GetValueOrDefault(server.Key, string.Empty)));
            }
        }
    }

    // The move of an old film or episode, or whether it still waits: an old stub with no item, or whose title is gone, has
    // nothing to carry.
    private Step TitleMoveOf(string root, StubState state, StubEntry old, Catalog catalog, bool automatic)
    {
        if (_library.FindByPath(FederatedPaths.Resolve(root, old.RelativePath), false) is not Video item)
        {
            return Step.None;
        }

        var news = NewStubsOf(state, old);
        if (news.Count == 0)
        {
            return FateOf(old.Sources, catalog);
        }

        return TargetsOf(root, news) is { } targets ? new Step(WatchStateMove.Of(old, item.Id, targets, automatic), false, []) : Step.Waiting;
    }

    private Step SeriesMoveOf(string root, StubState state, SeriesStub old, Catalog catalog, bool automatic)
    {
        if (_library.FindByPath(FederatedPaths.Resolve(root, old.RelativePath), true) is not Series item)
        {
            return Step.None;
        }

        var news = NewSeriesOf(state, old);
        if (news.Count == 0)
        {
            return FateOf(old.Sources, catalog);
        }

        var targets = new List<WatchStateTarget>();
        foreach (var series in news)
        {
            if (_library.FindByPath(FederatedPaths.Resolve(root, series.RelativePath), true) is not Series found)
            {
                return Step.Waiting;
            }

            targets.Add(new WatchStateTarget(found.Id, series.ServerId));
        }

        return new Step(WatchStateMove.OfSeries(old, item.Id, targets, automatic), false, []);
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
        var stubs = _remover.RemoveAll(root, state, state.Stubs.Where(stub => IsOld(stub.RelativePath)).ToList());
        var series = _remover.RemoveSeries(root, stubs.State, stubs.State.Series.Where(entry => IsOld(entry.RelativePath)).ToList());

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

    // The admin's libraries on an old path, with the new folders of the same type they lack, whether the new folders of each
    // server are in some library, and those in none; only the types with old titles count (Q249). Null when unreadable.
    private Libraries? LibrariesOf(string root, StubState state, IReadOnlySet<string>? skipped = null)
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

        var servers = _servers.GetServers().Select(server => server.Id).Distinct().ToList();
        var (imported, keptByPlugin, own) = _store.Read(config => (
            config.Servers.Where(server => servers.Contains(server.Id)).DistinctBy(server => server.Id).ToDictionary(server => server.Id, LibraryPlan.TypeFolders),
            config.Servers.Where(server => server.AutomaticLibraries && server.LibraryMode != LibraryMode.None).Select(server => server.Id).ToHashSet(),
            OwnLibraries(config)));
        var needed = TypeFolder.Written.Where(type => HasOldTitlesOf(state, type)).ToList();
        List<string> NewOf(Guid? server, TypeFolder type) =>
            servers.Where(id => server is null || id == server).SelectMany(id => NewFoldersOf(root, state, imported, id, type)).ToList();
        bool Linked(string path) => folders.Any(folder => Holds(folder, path));

        // The plugin adds the new folders of a server to its own libraries by itself (§10 row 445).
        var unlinked = servers
            .Where(server => !keptByPlugin.Contains(server))
            .SelectMany(server => needed.SelectMany(type => NewFoldersOf(root, state, imported, server, type)))
            .Where(path => !Linked(path))
            .ToList();
        var libraries = new List<LayoutMigrationLibrary>();
        var adds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var folder in folders.Where(folder => !(skipped?.Contains(KeyOf(folder)) ?? false) && !IsOwn(folder, own)))
        {
            var old = (folder.Locations ?? []).Select(location => OldFolderOf(root, location)).OfType<OldFolder>().DistinctBy(found => found.Path).ToList();
            if (old.Count > 0)
            {
                var missing = old.Where(found => needed.Contains(found.Type)).SelectMany(found => NewOf(found.Server, found.Type)).Distinct(StringComparer.Ordinal).Where(path => !Holds(folder, path)).ToList();
                libraries.Add(new LayoutMigrationLibrary(folder.Name, old.Select(found => found.Path).ToList(), missing));
                adds[folder.Name] = old.SelectMany(found => NewOf(found.Server, found.Type)).Distinct(StringComparer.Ordinal).Where(path => !Holds(folder, path)).ToList();
            }
        }

        var listed = libraries.SelectMany(library => library.MissingPaths).ToHashSet(StringComparer.Ordinal);
        return new Libraries(libraries, unlinked.Count == 0, unlinked.Where(path => !listed.Contains(path)).Distinct(StringComparer.Ordinal).ToList(), adds);
    }

    // The new folders of a server for the old folders of one type: those holding its titles and those of its imported libraries.
    private static IEnumerable<string> NewFoldersOf(string root, StubState state, IReadOnlyDictionary<Guid, IReadOnlyList<string>> imported, Guid server, TypeFolder type) =>
        StubLayout.TypeFoldersOf(state, server, type)
            .Concat(imported.GetValueOrDefault(server, []).Where(folder => string.Equals(Path.GetFileName(folder), type.Name, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(folder => FederatedPaths.Resolve(root, folder));

    // The flat Film or Serie, or a 0.5 folder of a server, as the path the plugin knows it by; null for any other location.
    private static OldFolder? OldFolderOf(string root, string location)
    {
        var relative = FederatedPaths.RelativeTo(root, Path.TrimEndingDirectorySeparator(location));
        if (TypeFolder.Written.FirstOrDefault(type => string.Equals(relative, type.Name, StringComparison.Ordinal)) is { } flat)
        {
            return new OldFolder(null, flat, Path.Combine(root, flat.Name));
        }

        return relative?.Split(Path.DirectorySeparatorChar) is [_, _] && LayoutFolderOf(root, location) is { } found
            ? new OldFolder(found.Server, found.Type, Path.Combine(FederatedFolderLayout.ServerPath(root, found.Server), found.Type.Name))
            : null;
    }

    // The libraries the plugin made, adopted or shares, by id or else by name, which the plugin itself keeps (§10 rows 437-442).
    private static List<(string ItemId, string Name)> OwnLibraries(PluginConfiguration config) =>
        config.Servers.SelectMany(server => server.AutomaticLibraryRegistry)
            .Concat(config.RemovedServerLibraries.SelectMany(held => held.Libraries))
            .Concat(config.SharedLibraries.Libraries)
            .Select(record => (record.ItemId ?? string.Empty, record.Name ?? string.Empty))
            .ToList();

    // Found as the manager of the libraries finds a record: by id, or else by name.
    private static bool IsOwn(VirtualFolderInfo folder, List<(string ItemId, string Name)> own) => own.Any(record =>
        (record.ItemId.Length > 0 && string.Equals(folder.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase))
        || string.Equals(folder.Name, record.Name, StringComparison.Ordinal));

    // Unlinked: the new folders in no library, less those listed as missing from a library on an old path. Adds: per
    // library, the new folders of every old folder it holds, also of types with no old titles.
    private sealed record Libraries(List<LayoutMigrationLibrary> OnOldPaths, bool ServerFoldersLinked, List<string> Unlinked, Dictionary<string, List<string>> Adds);

    // An old folder: flat when it has no server.
    private sealed record OldFolder(Guid? Server, TypeFolder Type, string Path);

    private sealed record LastRun(int Waiting, int Blocked, int Held);

    // An old title's move, or why it has none: it waits, empty imported libraries hold it, or there is nothing to carry.
    private sealed record Step(WatchStateMove? Move, bool Waits, IReadOnlyList<(Guid ServerId, string LibraryId)> Held)
    {
        public static Step None { get; } = new(null, false, []);

        public static Step Waiting { get; } = new(null, true, []);
    }

    // The index with the configured servers and the ones the admin removed, read once per run.
    private sealed record Catalog(IndexItemLookup Items, IReadOnlyDictionary<Guid, RemoteServerInfo> Configured, HashSet<Guid> Removed);
}
