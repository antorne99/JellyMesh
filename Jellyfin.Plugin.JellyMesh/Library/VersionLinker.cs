using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Links the stubs of a title across the server folders, and its local copy, as versions of one item (§3.4, §3.5).
/// Runs after each materialization, so a link Jellyfin or the administrator undid is made again (D5).
/// </summary>
public sealed class VersionLinker : IVersionLinker
{
    private readonly StubRegistry _registry;
    private readonly IFederationIndex _index;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly StubItemUpdater _updater;
    private readonly ILogger<VersionLinker> _logger;
    // Up until the first full run ends, so that a refusal after a restart is not taken as final (Q324).
    private volatile bool _lastRunFailed = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="VersionLinker"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="index">The index, for the quality of the stubs.</param>
    /// <param name="store">The configuration.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="updater">What saves the items.</param>
    /// <param name="logger">The logger.</param>
    public VersionLinker(
        StubRegistry registry,
        IFederationIndex index,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        StubItemUpdater updater,
        ILogger<VersionLinker> logger)
    {
        _registry = registry;
        _index = index;
        _store = store;
        _library = library;
        _users = users;
        _userData = userData;
        _updater = updater;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool LastRunFailed => _lastRunFailed;

    /// <inheritdoc />
    public Task LinkAsync(bool thorough, CancellationToken cancellationToken) => LinkGroupsAsync((groups, _) => groups, true, thorough, cancellationToken);

    /// <inheritdoc />
    public Task LinkLocalAsync(IReadOnlyCollection<Guid> localItemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localItemIds);

        var local = localItemIds.ToHashSet();
        return local.Count == 0
            ? Task.CompletedTask
            : LinkGroupsAsync((groups, run) => GroupsReaching(groups, local, run), false, false, cancellationToken);
    }

    // Links the groups chosen, as the full run links them. Only a full run clears the failed flag; any failure of any run
    // sets it, also one outside the catch of a group (Q324).
    private async Task LinkGroupsAsync(Func<IReadOnlyList<VersionGroup>, Run, IEnumerable<VersionGroup>> choose, bool full, bool thorough, CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Failed until the run ends, so that one stopped by an error or a cancellation counts as failed (Q259).
            if (full)
            {
                _lastRunFailed = true;
            }

            var failed = false;
            var state = _registry.State;
            var run = new Run(root, thorough, _index, _library);
            run.KnowItemsOf(state.Stubs);
            var recordedById = new Dictionary<string, string?>(StringComparer.Ordinal);
            var changed = false;

            // A renamed stub waits until its old item is gone: the old item owns the new one, and its group keeps its links.
            var renaming = state.Renames.Select(rename => rename.StubId).ToHashSet(StringComparer.Ordinal);

            // By group ID, so that of two groups that reach one local film the same one links to it every run (Q170).
            foreach (var group in choose(state.Groups, run).OrderBy(group => group.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recorded = group.PrimaryStubId;
                if (group.StubIds.Any(renaming.Contains))
                {
                    recordedById[group.Id] = recorded;
                    continue;
                }

                try
                {
                    recorded = await LinkGroupAsync(group, run, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The type only: a message could hold a remote address.
                    _logger.LogError("Cannot link the versions of a title: {Error}", exception.GetType().Name);
                    failed = true;
                }

                changed |= !string.Equals(recorded, group.PrimaryStubId, StringComparison.Ordinal);
                recordedById[group.Id] = recorded;
            }

            var groups = state.Groups.Select(group => group with { PrimaryStubId = recordedById.GetValueOrDefault(group.Id, group.PrimaryStubId) }).ToList();

            if (thorough)
            {
                var stuck = await DetachUngroupedAsync(state, state.Stubs.Where(stub => !renaming.Contains(stub.Id)), run, cancellationToken).ConfigureAwait(false);
                failed |= stuck.Count > 0;
            }

            await MoveCollectionEntriesAsync(run, cancellationToken).ConfigureAwait(false);
            if (changed)
            {
                await _registry.SaveAsync(state with { Groups = groups }, cancellationToken).ConfigureAwait(false);
            }

            _lastRunFailed = full ? failed : _lastRunFailed || failed;
        }
        catch
        {
            _lastRunFailed = true;
            throw;
        }
        finally
        {
            _registry.Exit();
        }
    }

    // The groups that hold one of the local copies, and the groups whose local film is the same, which may claim it first
    // (Q170).
    private List<VersionGroup> GroupsReaching(IReadOnlyList<VersionGroup> groups, HashSet<Guid> local, Run run)
    {
        Guid? HeadOf(VersionGroup group) => group.LocalItemIds.Count == 0 ? null : LocalVersionHead.Of(group.LocalItemIds, _library, run.IsStub)?.Id;

        var holding = groups.Where(group => group.LocalItemIds.Any(local.Contains)).ToList();
        var heads = holding.Select(HeadOf).OfType<Guid>().ToHashSet();
        return groups.Where(group => holding.Contains(group) || (HeadOf(group) is { } head && heads.Contains(head))).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GroupedStubsOf(IReadOnlyCollection<MergedCopy> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);

        var remote = copies.Where(copy => !copy.IsLocal)
            .Select(copy => (Server: Guid.TryParse(copy.ServerId, out var server) ? server : Guid.Empty, copy.ItemId, copy.Kind))
            .ToList();
        var episodes = remote.Where(copy => copy.Kind != ContentKind.Movie)
            .SelectMany(copy => _registry.FindEpisodesOfSeries(copy.Server, copy.ItemId))
            .Select(stub => stub.Id)
            .ToHashSet(StringComparer.Ordinal);
        var local = copies.Where(copy => copy.IsLocal)
            .Select(copy => Guid.TryParse(copy.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        bool Holds(StubEntry stub) => episodes.Contains(stub.Id) || (stub.Kind == StubKind.Movie && stub.Sources.Any(source => remote.Exists(copy =>
            copy.Kind == ContentKind.Movie && copy.Server == source.ServerId && string.Equals(copy.ItemId, source.ItemId, StringComparison.OrdinalIgnoreCase))));

        return _registry.State.Groups
            .Where(group => group.LocalItemIds.Any(local.Contains) || group.StubIds.Select(_registry.Find).OfType<StubEntry>().Any(Holds))
            .SelectMany(group => group.StubIds)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task DetachAsync(IReadOnlyCollection<string> stubIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stubIds);

        var root = _store.Read(config => config.FederatedFolderPath);
        if (string.IsNullOrEmpty(root) || stubIds.Count == 0)
        {
            return;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _registry.State;
            var named = stubIds.ToHashSet(StringComparer.Ordinal);
            var run = new Run(root, false, _index, _library);
            run.KnowItemsOf(state.Stubs.Where(stub => named.Contains(stub.Id)));
            await DetachUngroupedAsync(state, state.Stubs.Where(stub => named.Contains(stub.Id)), run, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registry.Exit();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StubEntry>> DetachForRemovalAsync(IReadOnlyCollection<StubEntry> stubs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stubs);

        var root = _store.Read(config => config.FederatedFolderPath);
        if (string.IsNullOrEmpty(root) || stubs.Count == 0)
        {
            return [];
        }

        var run = new Run(root, true, _index, _library);
        run.KnowItemsOf(stubs);
        return await DetachEachAsync(stubs, run, cancellationToken).ConfigureAwait(false);
    }

    private static VersionLinkState StateOf(Video video) => new(
        video.Id,
        video.PrimaryVersionId,
        video.LinkedAlternateVersions.Select(child => child.ItemId).OfType<Guid>().ToList());

    private static ItemLinks LinksOf(Video video, Run run) => new(StateOf(video), video.OwnerId, run.IsStub(video));

    private static LinkedChild[] LinksOf(Video video, IReadOnlyList<Guid> ids)
    {
        var current = new Dictionary<Guid, LinkedChild>();
        foreach (var child in video.LinkedAlternateVersions)
        {
            if (child.ItemId is { } id)
            {
                current.TryAdd(id, child);
            }
        }

        return ids.Select(id => current.GetValueOrDefault(id) ?? new LinkedChild { ItemId = id, Type = LinkedChildType.LinkedAlternateVersion }).ToArray();
    }

    // Returns the stub to record as primary.
    private async Task<string?> LinkGroupAsync(VersionGroup group, Run run, CancellationToken cancellationToken)
    {
        var items = new Dictionary<Guid, ItemLinks>();
        var stubs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var stub in group.StubIds.Select(_registry.Find).OfType<StubEntry>())
        {
            if (run.FindStub(stub) is { } links && items.TryAdd(links.Id, links))
            {
                stubs[stub.Id] = links.Id;
            }
        }

        // Only the first local copy found is linked: two local films are never merged (decision 20). A film an earlier
        // group links to stays out, and this group links its stubs among themselves (Q170).
        var locals = new List<Guid>();
        if (LocalVersionHead.Of(group.LocalItemIds, _library, run.IsStub) is { } local && run.ClaimedLocals.Add(local.Id) && items.TryAdd(local.Id, LinksOf(local, run)))
        {
            locals.Add(local.Id);
        }

        if (items.Count < 2)
        {
            // Only the nightly run detaches, so that an item missing for a moment is not detached and linked again (Q144).
            if (run.Thorough && items.Count == 1 && items.Values.Single().OwnerId.Equals(Guid.Empty))
            {
                await ApplyAsync(items.Keys.Single(), [], items, run, cancellationToken).ConfigureAwait(false);
            }

            return group.PrimaryStubId;
        }

        var primary = VersionLinkPlanner.ChoosePrimary(locals, stubs, group.PrimaryStubId, candidates => run.Best(candidates, _registry));
        var alternates = items.Keys.Where(id => id != primary.ItemId).ToList();
        var plan = await ApplyAsync(primary.ItemId, alternates, items, run, cancellationToken).ConfigureAwait(false);

        // The nightly run also moves entries added to an alternate after it was linked (Q146).
        foreach (var moved in run.Thorough ? alternates : plan.Joined.Where(id => id != primary.ItemId))
        {
            run.Moves[moved] = primary.ItemId;
        }

        return primary.PrimaryStubId;
    }

    // Plans the links from the block read; only a group that must change is read again from Jellyfin's cache, planned
    // again from those instances and saved on them, so the cache never holds a stale copy (Q387).
    private async Task<VersionLinkPlan> ApplyAsync(Guid primary, IReadOnlyList<Guid> alternates, IReadOnlyDictionary<Guid, ItemLinks> members, Run run, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<Guid, ItemLinks>(members);
        foreach (var id in members.Values.SelectMany(links => links.State.LinkedIds.Append(links.State.PrimaryVersionId ?? Guid.Empty)))
        {
            if (id != Guid.Empty && !resolved.ContainsKey(id) && run.Find(id) is { } other)
            {
                resolved[id] = other;
            }
        }

        var known = resolved.Values.Where(links => members.ContainsKey(links.Id) || links.OwnerId.Equals(Guid.Empty)).Select(links => links.State).ToList();
        var plan = VersionLinkPlanner.Plan(primary, alternates, known, id => resolved.TryGetValue(id, out var links) && !links.IsStub);
        if (plan.Updates.Count == 0)
        {
            return plan;
        }

        var cached = new Dictionary<Guid, Video>();
        foreach (var id in members.Keys)
        {
            if (_library.GetItemById(id) is Video video)
            {
                cached[id] = video;
            }
        }

        return cached.ContainsKey(primary)
            ? await ApplyCachedAsync(primary, alternates.Where(cached.ContainsKey).ToList(), cached, run, cancellationToken).ConfigureAwait(false)
            : new VersionLinkPlan([], []);
    }

    // Plans the links of some members against the instances Jellyfin caches, aligns played if one joins, and saves the plan.
    private async Task<VersionLinkPlan> ApplyCachedAsync(Guid primary, IReadOnlyList<Guid> alternates, IReadOnlyDictionary<Guid, Video> members, Run run, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<Guid, Video>(members);
        foreach (var id in members.Values.SelectMany(video => StateOf(video).LinkedIds.Append(video.PrimaryVersionId ?? Guid.Empty)))
        {
            if (id != Guid.Empty && !resolved.ContainsKey(id) && _library.GetItemById(id) is Video other)
            {
                resolved[id] = other;
            }
        }

        // A version of a local film belongs to its owner: it is never saved.
        var known = resolved.Values.Where(video => members.ContainsKey(video.Id) || video.OwnerId.Equals(Guid.Empty)).ToDictionary(video => video.Id);
        var plan = VersionLinkPlanner.Plan(primary, alternates, known.Values.Select(StateOf).ToList(), id => resolved.TryGetValue(id, out var video) && !run.IsStub(video));
        if (alternates.Count > 0 && plan.Joined.Count > 0)
        {
            AlignPlayed(members.Values.ToList(), cancellationToken);
        }

        foreach (var update in plan.Updates)
        {
            await SaveLinksAsync(known[update.ItemId], update, cancellationToken).ConfigureAwait(false);
        }

        return plan;
    }

    // Detaches each of the stubs that is in no group from every item it is linked to, keeping only links between films (Q144).
    private Task<List<StubEntry>> DetachUngroupedAsync(StubState state, IEnumerable<StubEntry> stubs, Run run, CancellationToken cancellationToken)
    {
        var grouped = state.Groups.SelectMany(group => group.StubIds).ToHashSet(StringComparer.Ordinal);
        return DetachEachAsync(stubs.Where(stub => !grouped.Contains(stub.Id)), run, cancellationToken);
    }

    // Detaches each stub from every item it is linked to, keeping only links between films. A failure stops only that stub,
    // and the stubs that failed are returned.
    private async Task<List<StubEntry>> DetachEachAsync(IEnumerable<StubEntry> stubs, Run run, CancellationToken cancellationToken)
    {
        var failed = new List<StubEntry>();
        foreach (var stub in stubs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (run.FindStub(stub) is { OwnerId: var owner } links && owner.Equals(Guid.Empty)
                    && (links.State.PrimaryVersionId is not null || links.State.LinkedIds.Count > 0))
                {
                    await ApplyAsync(links.Id, [], new Dictionary<Guid, ItemLinks> { [links.Id] = links }, run, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type only: a message could hold a remote address.
                _logger.LogError("Cannot detach a version from its group: {Error}", exception.GetType().Name);
                failed.Add(stub);
            }
        }

        return failed;
    }

    // The cached item is put back if the save fails, or the next run would find it linked already.
    private async Task SaveLinksAsync(Video video, VersionLinkUpdate update, CancellationToken cancellationToken)
    {
        var (primary, links) = (video.PrimaryVersionId, video.LinkedAlternateVersions);
        video.LinkedAlternateVersions = LinksOf(video, update.LinkedIds);
        video.SetPrimaryVersionId(update.PrimaryVersionId);
        try
        {
            // Not MetadataEdit: Jellyfin would run its savers and write a .nfo next to a local copy (§10 row 93).
            await _updater.SaveAsync(video, ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            video.LinkedAlternateVersions = links;
            video.SetPrimaryVersionId(primary);
            throw;
        }
    }

    // D4: the version played last sets the played flag of the others. The position stays with each version.
    private void AlignPlayed(IReadOnlyList<Video> members, CancellationToken cancellationToken)
    {
        foreach (var user in _users.GetUsers().ToList())
        {
            var data = members.Select(member => (Item: member, Data: _userData.GetUserData(user, member))).ToList();
            if (PlayedAlignment.Decide(data.Select(entry => entry.Data)) is not { } played)
            {
                continue;
            }

            foreach (var (item, entry) in data)
            {
                if (entry is not null && entry.Played != played)
                {
                    entry.Played = played;
                    _userData.SaveUserData(user, item, entry, UserDataSaveReason.Import, cancellationToken);
                }
            }
        }
    }

    // Collections follow the primary as MergeVersions does; playlists keep the version that was added (Q79). A collection
    // whose save fails is put back, so that a later run finds the entry where it still is.
    private async Task MoveCollectionEntriesAsync(Run run, CancellationToken cancellationToken)
    {
        if (run.Moves.Count == 0)
        {
            return;
        }

        foreach (var collection in run.Collections().Where(collection => collection.LinkedChildren.Any(child => child.ItemId is { } id && run.Moves.ContainsKey(id))))
        {
            var seen = new HashSet<Guid>();
            var children = new List<LinkedChild>(collection.LinkedChildren.Length);
            foreach (var child in collection.LinkedChildren)
            {
                var target = child.ItemId is { } id && run.Moves.TryGetValue(id, out var primaryId) && _library.GetItemById(primaryId) is { } primary ? LinkedChild.Create(primary) : child;
                if (target.ItemId is not { } targetId || seen.Add(targetId))
                {
                    children.Add(target);
                }
            }

            var before = collection.LinkedChildren;
            collection.LinkedChildren = children.ToArray();
            try
            {
                await _updater.SaveAsync(collection, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                collection.LinkedChildren = before;
                _logger.LogError("Cannot move the entries of a collection to the primary version: {Error}", exception.GetType().Name);
            }
        }
    }

    // What one run looks up once: the quality of the stubs and the collections.
    private sealed class Run(string root, bool thorough, IFederationIndex index, ILibraryManager library)
    {
        private const int KnownChunk = 1000;

        private readonly Dictionary<Guid, ItemLinks> _known = [];
        private IndexItemLookup? _lookup;
        private List<BoxSet>? _collections;

        // The primary that each alternate's collection entries move to.
        public Dictionary<Guid, Guid> Moves { get; } = [];

        // The local films a group already links to in this run.
        public HashSet<Guid> ClaimedLocals { get; } = [];

        public bool Thorough => thorough;

        public bool IsStub(BaseItem item) => FederatedPaths.IsInside(root, item.Path);

        // Reads the links of the stubs' items in a few queries, keeping only their link state: one lookup per stub was most
        // of a sync on a large catalog (Q387).
        public void KnowItemsOf(IEnumerable<StubEntry> stubs)
        {
            _known.Clear();
            foreach (var chunk in stubs.SelectMany(IdsOf).Distinct().Chunk(KnownChunk))
            {
                foreach (var video in library.GetItemList(new InternalItemsQuery { ItemIds = chunk }).OfType<Video>())
                {
                    _known[video.Id] = LinksOf(video, this);
                }
            }
        }

        public ItemLinks? FindStub(StubEntry stub)
        {
            if (IdsOf(stub).Select(id => _known.GetValueOrDefault(id)).OfType<ItemLinks>().FirstOrDefault() is { } links)
            {
                return links;
            }

            return thorough && library.FindByPath(FederatedPaths.Resolve(root, stub.RelativePath), false) is Video video ? LinksOf(video, this) : null;
        }

        // The links of any item, from the block read or else looked up.
        public ItemLinks? Find(Guid id) => _known.TryGetValue(id, out var links) ? links : library.GetItemById(id) is Video video ? LinksOf(video, this) : null;

        // The item of the stub's path as Jellyfin names a film or an episode, then as a plain video.
        private Guid[] IdsOf(StubEntry stub)
        {
            var path = FederatedPaths.Resolve(root, stub.RelativePath);
            Type kind = stub.Kind == StubKind.Episode ? typeof(Episode) : typeof(Movie);
            return [library.GetNewItemId(path, kind), library.GetNewItemId(path, typeof(Video))];
        }

        public string Best(IReadOnlyList<string> stubIds, StubRegistry registry)
        {
            _lookup ??= IndexItemLookup.Build(index.Snapshot);
            return StubRanking.Best(stubIds, registry.Find, _lookup);
        }

        public IReadOnlyList<BoxSet> Collections()
        {
            _collections ??= library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet], Recursive = true })
                .Select(item => library.GetItemById(item.Id))
                .OfType<BoxSet>()
                .ToList();
            return _collections;
        }
    }

    // The link state of one item, without the item: a run keeps one for each stub (Q387).
    private sealed record ItemLinks(VersionLinkState State, Guid OwnerId, bool IsStub)
    {
        public Guid Id => State.ItemId;
    }
}
