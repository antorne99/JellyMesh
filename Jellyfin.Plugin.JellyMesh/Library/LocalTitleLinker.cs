using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILocalTitleLinker"/> through the planner of the materializer: it plans the whole catalog in memory, but
/// saves only the version groups that hold the local copies of the title, and links only those.
/// </summary>
public sealed class LocalTitleLinker : ILocalTitleLinker
{
    private readonly StubRegistry _registry;
    private readonly IFederationResolver _resolver;
    private readonly IFederationIndex _index;
    private readonly IRemoteServerRegistry _servers;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IVersionLinker _linker;
    private readonly ILogger<LocalTitleLinker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalTitleLinker"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="resolver">The resolver.</param>
    /// <param name="index">The index.</param>
    /// <param name="servers">The server registry.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="linker">What links the versions.</param>
    /// <param name="logger">The logger.</param>
    public LocalTitleLinker(
        StubRegistry registry,
        IFederationResolver resolver,
        IFederationIndex index,
        IRemoteServerRegistry servers,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IVersionLinker linker,
        ILogger<LocalTitleLinker> logger)
    {
        _registry = registry;
        _resolver = resolver;
        _index = index;
        _servers = servers;
        _store = store;
        _library = library;
        _linker = linker;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task LinkAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (_store.Read(config => config.FederatedFolderPath).Length == 0)
        {
            return;
        }

        var local = LocalCopiesOf(itemId);
        if (local.Count == 0)
        {
            return;
        }

        bool found;
        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            found = await SaveGroupsAsync(local, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registry.Exit();
        }

        if (found)
        {
            await _linker.LinkLocalAsync(local, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Linked a new local title to its copies on the remote servers as soon as Jellyfin read its metadata");
        }
    }

    /// <summary>
    /// Puts the planned groups of one title in place of the groups they replace. Null when that cannot be done without
    /// touching another title: a planned group with a stub not written yet, or a group that would lose stubs.
    /// </summary>
    /// <param name="current">The groups saved now.</param>
    /// <param name="planned">The groups of the title, as a full materialization would save them.</param>
    /// <param name="stubIds">The stubs saved now.</param>
    /// <returns>The groups to save, or null.</returns>
    internal static List<VersionGroup>? Replace(IReadOnlyList<VersionGroup> current, IReadOnlyList<VersionGroup> planned, IReadOnlySet<string> stubIds)
    {
        if (planned.Any(group => !group.StubIds.All(stubIds.Contains)))
        {
            return null;
        }

        bool Touches(VersionGroup group, VersionGroup other) =>
            string.Equals(group.Id, other.Id, StringComparison.Ordinal)
            || group.StubIds.Intersect(other.StubIds, StringComparer.Ordinal).Any()
            || group.LocalItemIds.Intersect(other.LocalItemIds).Any();

        var replaced = current.Where(group => planned.Any(other => Touches(group, other))).ToList();
        var safe = replaced.TrueForAll(group => planned.Any(other =>
            string.Equals(group.Id, other.Id, StringComparison.Ordinal) && group.StubIds.All(other.StubIds.Contains)));
        return safe ? current.Except(replaced).Concat(planned).ToList() : null;
    }

    // The lists of a group compare by reference, so a group is compared by what it holds.
    private static string KeyOf(VersionGroup group) =>
        string.Join('|', group.Id, group.PrimaryStubId, string.Join(',', group.StubIds), string.Join(',', group.LocalItemIds));

    // A film is its own copy; a series is matched by its episodes.
    private List<Guid> LocalCopiesOf(Guid itemId)
    {
        if (_library.GetItemById(itemId) is not Series)
        {
            return [itemId];
        }

        return _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Episode],
                AncestorIds = [itemId],
                Recursive = true,
                IsVirtualItem = false
            })
            .Select(episode => episode.Id)
            .ToList();
    }

    // Returns whether a group holds a local copy of the title. Only the planned groups of the title are saved; the stubs,
    // their files and the other groups wait for the next sync.
    private async Task<bool> SaveGroupsAsync(IReadOnlyCollection<Guid> local, CancellationToken cancellationToken)
    {
        var snapshot = _index.Snapshot;
        var servers = _servers.GetServers();
        var view = _resolver.Resolve(snapshot, servers);
        var state = _registry.State;
        var plan = StubPlanner.PlanByServer(new StubPlanInput(view, snapshot, state, servers, false, _ => string.Empty, _ => null));

        var planned = plan.State.Groups.Where(group => group.LocalItemIds.Any(local.Contains)).ToList();
        if (planned.Count == 0)
        {
            return false;
        }

        var groups = Replace(state.Groups, planned, state.Stubs.Select(stub => stub.Id).ToHashSet(StringComparer.Ordinal));
        if (groups is null)
        {
            _logger.LogInformation("A new local title is linked to its copies on the remote servers at the next sync");
            return false;
        }

        if (!groups.Select(KeyOf).ToHashSet(StringComparer.Ordinal).SetEquals(state.Groups.Select(KeyOf)))
        {
            await _registry.SaveAsync(state with { Groups = groups }, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}
