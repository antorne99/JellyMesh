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
/// the rows of a deleted item stay behind a placeholder. A local copy no longer removes a stub (Q18 b). It is also where
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
        IServerStubRenamer? renames = null)
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
            var state = await MergeStubsAsync(root, _registry.State, cancellationToken).ConfigureAwait(false);
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

    private async Task<StubState> MergeStubsAsync(string root, StubState state, CancellationToken cancellationToken)
    {
        // One snapshot and one server list, so that the claims are those the materializer made.
        var snapshot = _index.Snapshot;
        var servers = _servers.GetServers();
        var view = _resolver.Resolve(snapshot, servers);
        if (view.Health == IndexHealth.Corrupt)
        {
            return state;
        }

        var users = _users.GetUsers().ToList();
        foreach (var claim in StubPlanner.ClaimByServer(view, snapshot, servers, state.Stubs))
        {
            if (claim.Stub is not { } stub)
            {
                continue;
            }

            try
            {
                foreach (var merged in claim.Merged)
                {
                    state = await MergeAsync(root, state, merged, stub, users, cancellationToken).ConfigureAwait(false);
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

    private async Task<StubState> MergeAsync(string root, StubState state, StubEntry stub, StubEntry into, List<User> users, CancellationToken cancellationToken)
    {
        // Without its item, which only a scan makes, the survivor cannot receive anything yet.
        if (_library.FindByPath(FederatedPaths.Resolve(root, into.RelativePath), false) is not { } survivor)
        {
            return state;
        }

        // A stub that cannot leave its version group stays for the next scan, so Jellyfin rewrites no .nfo (Q326, Q327).
        if (_linker is not null && (await _linker.DetachForRemovalAsync([stub], cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return state;
        }

        if (_library.FindByPath(FederatedPaths.Resolve(root, stub.RelativePath), false) is { } stubItem)
        {
            foreach (var user in users)
            {
                if (_userData.GetUserData(user, stubItem) is { } data)
                {
                    HandOver(user, data, survivor, WatchStateMerge.Merge, cancellationToken);
                }
            }
        }

        return _remover.Remove(root, state, stub);
    }

    private void HandOver(User user, UserItemData from, BaseItem target, Func<UserItemData, UserItemData, bool> merge, CancellationToken cancellationToken)
    {
        var data = _userData.GetUserData(user, target);
        if (data is not null && merge(from, data))
        {
            _userData.SaveUserData(user, target, data, UserDataSaveReason.Import, cancellationToken);
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
