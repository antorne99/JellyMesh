using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Runs after every library scan. When two copies of one server, shown apart until now, became one title, it hands the
/// watch state of each user over to the stub that stays and only then removes the other: Jellyfin would not do it, and
/// the rows of a deleted item stay behind a placeholder. A stub that split from another, in another local library, takes its
/// watch state here once it has an item (§10 row 507). A local copy no longer removes a stub (Q18 b). It is also where
/// a new stub series gets its refresh, since the item exists only after the scan, and where a new stub is first linked to
/// its versions (Q145).
/// </summary>
/// <remarks>
/// Stubs of the flat layout are left as they are until the migration. A corrupt index holds no decision at all.
/// </remarks>
public sealed class StubCleanupTask : ILibraryPostScanTask
{
    private readonly StubRegistry _registry;
    private readonly IFederationResolver _resolver;
    private readonly IFederationIndex _index;
    private readonly IRemoteServerRegistry _servers;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly StubRemover _remover;
    private readonly ILogger<StubCleanupTask> _logger;
    private readonly IVersionLinker? _linker;
    private readonly ILayoutMigration? _migration;
    private readonly IServerStubRenamer? _renames;
    private readonly IWatchStateKeeper? _keeper;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubCleanupTask"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="resolver">The resolver.</param>
    /// <param name="index">The index.</param>
    /// <param name="servers">The server registry.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="providers">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="remover">The stub remover.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="linker">The version linker, if any.</param>
    /// <param name="migration">The manual migration from the flat layout, if any.</param>
    /// <param name="renames">The stub moves of renamed servers, if any.</param>
    /// <param name="keeper">What puts back the watch state of removed servers (row 412).</param>
    public StubCleanupTask(
        StubRegistry registry,
        IFederationResolver resolver,
        IFederationIndex index,
        IRemoteServerRegistry servers,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        IProviderManager providers,
        IFileSystem fileSystem,
        StubRemover remover,
        ILogger<StubCleanupTask> logger,
        IVersionLinker? linker = null,
        ILayoutMigration? migration = null,
        IServerStubRenamer? renames = null,
        IWatchStateKeeper? keeper = null)
    {
        _registry = registry;
        _resolver = resolver;
        _index = index;
        _servers = servers;
        _store = store;
        _library = library;
        _users = users;
        _userData = userData;
        _providers = providers;
        _fileSystem = fileSystem;
        _remover = remover;
        _logger = logger;
        _linker = linker;
        _migration = migration;
        _renames = renames;
        _keeper = keeper;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0)
        {
            return;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await MergeStubsAsync(root, HandOverSplits(root, _registry.State, cancellationToken), cancellationToken).ConfigureAwait(false);
            state = RefreshNewSeries(root, state);
            if (!ReferenceEquals(state, _registry.State))
            {
                await _registry.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _registry.Exit();
        }

        await FinishRenamesAsync(cancellationToken).ConfigureAwait(false);
        await LinkAsync(cancellationToken).ConfigureAwait(false);

        // The new stubs have their items now, and their versions are linked (row 412).
        if (_keeper is not null)
        {
            await _keeper.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }

        await MigrateAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    // After the scan of the new paths of a renamed server, the old stubs hand over their state and go (Q38).
    private async Task FinishRenamesAsync(CancellationToken cancellationToken)
    {
        if (_renames is null)
        {
            return;
        }

        try
        {
            await _renames.FinishAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Could not finish moving the stubs of a renamed server: {Error}", exception.GetType().Name);
        }
    }

    // After the scan of the new paths and the link of their versions, the old stubs can hand over their state (Q40).
    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if (_migration is null)
        {
            return;
        }

        try
        {
            await _migration.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Could not migrate the old stubs to the server folders: {Error}", exception.GetType().Name);
        }
    }

    // The linker takes the registry itself, so it runs once the merged stubs are gone and the lock is free.
    private async Task LinkAsync(CancellationToken cancellationToken)
    {
        if (_linker is null)
        {
            return;
        }

        try
        {
            await _linker.LinkAsync(false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Could not link the versions of the titles after the scan: {Error}", exception.GetType().Name);
        }
    }

    // A stub or series that split takes the state of the one it split from, once: later each keeps its own (row 63). It waits
    // until a scan has made both items.
    private StubState HandOverSplits(string root, StubState state, CancellationToken cancellationToken)
    {
        if (state.Splits.Count == 0)
        {
            return state;
        }

        // No users may mean they cannot be read for now.
        var users = _users.GetUsers().ToList();
        var stubs = state.Stubs.ToDictionary(stub => stub.Id, stub => stub.RelativePath, StringComparer.Ordinal);
        var series = state.Series.ToDictionary(entry => entry.Id, entry => entry.RelativePath, StringComparer.Ordinal);
        var left = state.Splits.Where(split => IsKnown(split.StubId) && IsKnown(split.FromStubId)
            && (users.Count == 0
                || !(HandOverSplit(root, split, stubs, false, WatchStateMerge.Merge, users, cancellationToken)
                    || HandOverSplit(root, split, series, true, MergeSeries, users, cancellationToken)))).ToList();
        return left.Count == state.Splits.Count ? state : state with { Splits = left };

        bool IsKnown(string id) => stubs.ContainsKey(id) || series.ContainsKey(id);
    }

    private bool HandOverSplit(string root, StubSplit split, Dictionary<string, string> paths, bool isFolder, Func<UserItemData, UserItemData, bool> merge, List<User> users, CancellationToken cancellationToken)
    {
        if (!paths.TryGetValue(split.FromStubId, out var from) || !paths.TryGetValue(split.StubId, out var to))
        {
            return false;
        }

        try
        {
            if (_library.FindByPath(FederatedPaths.Resolve(root, from), isFolder) is not { } source)
            {
                return IsGone(root, from, isFolder);
            }

            if (_library.FindByPath(FederatedPaths.Resolve(root, to), isFolder) is not { } target)
            {
                return false;
            }

            HandOver(users, source, target, merge, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // It still waits, and the next scan tries again.
            _logger.LogError("Cannot hand over the watch state of a split title: {Error}", exception.GetType().Name);
            return false;
        }
    }

    // Whether the file of a stub or the folder of a series is gone. A type folder without its marker, as on a disk that is not
    // mounted, proves nothing.
    private static bool IsGone(string root, string relativePath, bool isFolder)
    {
        var path = FederatedPaths.Resolve(root, relativePath);
        return FederatedPaths.TypeFolderPathOf(relativePath) is { } folder
            && File.Exists(Path.Combine(FederatedPaths.Resolve(root, folder), FederatedPaths.HiddenFileName))
            && !(isFolder ? Directory.Exists(path) : File.Exists(path));
    }

    private async Task<StubState> MergeStubsAsync(string root, StubState state, CancellationToken cancellationToken)
    {
        // One snapshot and one server list, so that the claims are those the materializer made.
        var snapshot = _index.Snapshot;
        var servers = _servers.GetServers();
        var view = _resolver.Resolve(snapshot, servers);
        if (view.Health == IndexHealth.Corrupt || ReadPlacement() is not { } placement)
        {
            return state;
        }

        // No users may mean they cannot be read for now.
        var users = _users.GetUsers().ToList();
        if (users.Count == 0)
        {
            return state;
        }

        var merges = StubPlanner.ClaimByServer(view, snapshot, servers, state.Stubs, placement.KeyOf)
            .SelectMany(claim => claim.Merged.Select(merged => (Merged: merged, Into: claim.Stub!)))
            .GroupBy(merge => merge.Merged.Id, StringComparer.Ordinal);
        foreach (var merge in merges)
        {
            var merged = merge.First().Merged;
            var into = merge.Select(entry => entry.Into).ToList();
            try
            {
                if (into.TrueForAll(stub => IsTakenOver(merged, stub)) && !IsWaitedFor(state, merged, into))
                {
                    state = await MergeAsync(root, state, merged, into, users, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The stub stays, so its state is not lost, and the next scan tries again.
                _logger.LogError("Cannot merge a stub: {Error}", exception.GetType().Name);
            }
        }

        return state;
    }

    // Whether a split still waits for the state of these stubs: a merge would remove it, or mix it with that of other episodes.
    private static bool IsWaitedFor(StubState state, StubEntry merged, List<StubEntry> into) => state.Splits.Any(split =>
        split.StubId == merged.Id
        || split.FromStubId == merged.Id
        || split.FromStubId == merged.SeriesStubId
        || into.Exists(stub => stub.Id == split.FromStubId));

    // Null when the libraries cannot be read: no stub is merged until the next scan.
    private LibraryPlacement? ReadPlacement()
    {
        try
        {
            return LibraryPlacement.Read(_library, _store);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot read the libraries to merge the stubs: {Error}", exception.GetType().Name);
            return null;
        }
    }

    // Whether a sync already gave a copy of a merged stub to the one that stays: a library that changed since merges nothing.
    private static bool IsTakenOver(StubEntry merged, StubEntry into) => merged.Sources.Any(source =>
        into.Sources.Any(other => other.ServerId == source.ServerId && string.Equals(other.ItemId, source.ItemId, StringComparison.Ordinal))
        || (into.ServerId == source.ServerId && into.PartCopies.Contains(source.ItemId, StringComparer.Ordinal)));

    private async Task<StubState> MergeAsync(string root, StubState state, StubEntry stub, List<StubEntry> into, List<User> users, CancellationToken cancellationToken)
    {
        // Without its item, which only a scan makes, a survivor cannot receive anything yet.
        var survivors = new List<BaseItem>();
        foreach (var entry in into)
        {
            if (_library.FindByPath(FederatedPaths.Resolve(root, entry.RelativePath), false) is not { } survivor)
            {
                return state;
            }

            survivors.Add(survivor);
        }

        // A stub that cannot leave its version group, also at the old path of a pending server rename, stays for the next
        // scan, so Jellyfin rewrites no .nfo (Q326, Q327, Q351).
        if (_linker is not null && (await _linker.DetachForRemovalAsync(state.WithOldPaths([stub]), cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return state;
        }

        if (_library.FindByPath(FederatedPaths.Resolve(root, stub.RelativePath), false) is { } stubItem)
        {
            foreach (var survivor in survivors)
            {
                HandOver(users, stubItem, survivor, WatchStateMerge.Merge, cancellationToken);
            }
        }

        foreach (var entry in into.DistinctBy(entry => entry.SeriesStubId))
        {
            HandOverSeries(root, state, stub, entry, users, cancellationToken);
        }

        return _remover.Remove(root, state, stub);
    }

    // The series of the last merged episode goes with it, so the series that stays takes its favorite and rating.
    private void HandOverSeries(string root, StubState state, StubEntry stub, StubEntry into, List<User> users, CancellationToken cancellationToken)
    {
        if (stub.SeriesStubId is { } leaving
            && !string.Equals(leaving, into.SeriesStubId, StringComparison.Ordinal)
            && !state.Stubs.Any(other => other.Id != stub.Id && other.SeriesStubId == leaving)
            && FindSeries(root, state, leaving) is { } source
            && FindSeries(root, state, into.SeriesStubId) is { } target)
        {
            HandOver(users, source, target, MergeSeries, cancellationToken);
        }
    }

    private BaseItem? FindSeries(string root, StubState state, string? id) =>
        state.Series.FirstOrDefault(entry => entry.Id == id) is { } series ? _library.FindByPath(FederatedPaths.Resolve(root, series.RelativePath), true) : null;

    private static bool MergeSeries(UserItemData from, UserItemData to) => WatchStateTransfer.CarrySeries(from, [to]).Count > 0;

    private void HandOver(List<User> users, BaseItem source, BaseItem target, Func<UserItemData, UserItemData, bool> merge, CancellationToken cancellationToken)
    {
        // FindByPath gives an instance of its own: saved there, the one Jellyfin caches would keep the old rows.
        var item = _library.GetItemById(target.Id) ?? target;
        foreach (var user in users)
        {
            if (_userData.GetUserData(user, source) is { } from && _userData.GetUserData(user, item) is { } data && merge(from, data))
            {
                _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, cancellationToken);
            }
        }
    }

    private StubState RefreshNewSeries(string root, StubState state)
    {
        var pending = state.Series.Where(series => series.NeedsRefresh).ToList();
        if (pending.Count == 0)
        {
            return state;
        }

        var series = state.Series.ToList();
        foreach (var entry in pending)
        {
            if (_library.FindByPath(FederatedPaths.Resolve(root, entry.RelativePath), true) is not { } item)
            {
                continue;
            }

            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default
            };
            _providers.QueueRefresh(item.Id, options, RefreshPriority.Normal);
            series[series.IndexOf(entry)] = entry with { NeedsRefresh = false };
        }

        return state with { Series = series };
    }
}
