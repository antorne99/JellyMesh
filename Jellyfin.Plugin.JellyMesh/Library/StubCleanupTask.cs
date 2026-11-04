using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Runs after every library scan. When a local copy has arrived it hands the watch state of each user over to it and only
/// then removes the stub: Jellyfin would not do it, because a real file never sees the state of a stub, and the rows of a
/// deleted item stay behind a placeholder (decision 2). The same goes for a stub merged into another, when two copies
/// shown apart became one title. It is also where a new stub series gets its refresh, since the item exists only after
/// the scan.
/// </summary>
/// <remarks>
/// A local copy is a fact about this server, so a stale server does not stop it. A corrupt index holds no decision at all.
/// </remarks>
public sealed class StubCleanupTask : ILibraryPostScanTask
{
    private readonly StubRegistry _registry;
    private readonly IFederationResolver _resolver;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly StubRemover _remover;
    private readonly ILogger<StubCleanupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubCleanupTask"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="resolver">The resolver.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="providers">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="remover">The stub remover.</param>
    /// <param name="logger">The logger.</param>
    public StubCleanupTask(
        StubRegistry registry,
        IFederationResolver resolver,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        IProviderManager providers,
        IFileSystem fileSystem,
        StubRemover remover,
        ILogger<StubCleanupTask> logger)
    {
        _registry = registry;
        _resolver = resolver;
        _store = store;
        _library = library;
        _users = users;
        _userData = userData;
        _providers = providers;
        _fileSystem = fileSystem;
        _remover = remover;
        _logger = logger;
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
            var state = ReplaceStubs(root, _registry.State, cancellationToken);
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

        progress.Report(100);
    }

    private StubState ReplaceStubs(string root, StubState state, CancellationToken cancellationToken)
    {
        var view = _resolver.Resolve();
        if (view.Health == IndexHealth.Corrupt)
        {
            return state;
        }

        var claims = new StubMatcher(state.Stubs).ClaimAll(view.Contents);
        var users = _users.GetUsers().ToList();
        foreach (var claim in claims)
        {
            var (content, stub) = claim;
            if (stub is null)
            {
                continue;
            }

            try
            {
                foreach (var merged in claim.Merged)
                {
                    state = content.IsLocal
                        ? Replace(root, state, merged, content, view, users, cancellationToken)
                        : Merge(root, state, merged, stub, users, cancellationToken);
                }

                if (content.IsLocal)
                {
                    state = Replace(root, state, stub, content, view, users, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The stub stays, so its state is not lost, and the next scan tries again.
                _logger.LogError("Cannot replace a stub: {Error}", exception.GetType().Name);
            }
        }

        return state;
    }

    private StubState Replace(string root, StubState state, StubEntry stub, ResolvedContent content, FederationView view, List<User> users, CancellationToken cancellationToken)
    {
        var locals = content.LocalItemIds.Select(_library.GetItemById).OfType<BaseItem>().ToList();
        if (locals.Count == 0)
        {
            return state;
        }

        if (_library.FindByPath(FederatedPaths.Resolve(root, stub.RelativePath), false) is { } stubItem)
        {
            foreach (var user in users)
            {
                var data = _userData.GetUserData(user, stubItem);
                if (data is null)
                {
                    continue;
                }

                foreach (var local in locals)
                {
                    HandOver(user, data, local, WatchStateMerge.Merge, cancellationToken);
                }
            }
        }

        HandOverSeriesFavorite(root, state, stub, content, view, users, cancellationToken);
        return _remover.Remove(root, state, stub);
    }

    private StubState Merge(string root, StubState state, StubEntry stub, StubEntry into, List<User> users, CancellationToken cancellationToken)
    {
        // Without its item, which only a scan makes, the survivor cannot receive anything yet.
        if (_library.FindByPath(FederatedPaths.Resolve(root, into.RelativePath), false) is not { } survivor)
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

    // Only when the series row itself is about to go: a favorite set on the merged series may sit on the stub row.
    private void HandOverSeriesFavorite(string root, StubState state, StubEntry stub, ResolvedContent content, FederationView view, List<User> users, CancellationToken cancellationToken)
    {
        if (stub.SeriesStubId is null
            || state.Stubs.Any(entry => !string.Equals(entry.Id, stub.Id, StringComparison.Ordinal) && string.Equals(entry.SeriesStubId, stub.SeriesStubId, StringComparison.Ordinal))
            || state.Series.FirstOrDefault(entry => string.Equals(entry.Id, stub.SeriesStubId, StringComparison.Ordinal)) is not { } series
            || !view.LocalItemByWork.TryGetValue(content.Identity.WorkKey, out var localItemId)
            || _library.GetItemById(localItemId) is not Episode episode
            || _library.GetItemById(episode.SeriesId) is not Series localSeries
            || _library.FindByPath(FederatedPaths.Resolve(root, series.RelativePath), true) is not { } stubSeries)
        {
            return;
        }

        foreach (var user in users)
        {
            if (_userData.GetUserData(user, stubSeries) is { } data)
            {
                HandOver(user, data, localSeries, WatchStateMerge.MergeFavorite, cancellationToken);
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
