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
    public async Task LinkAsync(bool thorough, CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _registry.State;
            var run = new Run(root, thorough, _index, _library);
            var recordedById = new Dictionary<string, string?>(StringComparer.Ordinal);
            var changed = false;

            // By group ID, so that of two groups that reach one local film the same one links to it every run (Q170).
            foreach (var group in state.Groups.OrderBy(group => group.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recorded = group.PrimaryStubId;
                try
                {
                    recorded = await LinkGroupAsync(group, run, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The type only: a message could hold a remote address.
                    _logger.LogError("Cannot link the versions of a title: {Error}", exception.GetType().Name);
                }

                changed |= !string.Equals(recorded, group.PrimaryStubId, StringComparison.Ordinal);
                recordedById[group.Id] = recorded;
            }

            var groups = state.Groups.Select(group => group with { PrimaryStubId = recordedById.GetValueOrDefault(group.Id, group.PrimaryStubId) }).ToList();

            if (thorough)
            {
                await DetachUngroupedAsync(state, state.Stubs, run, cancellationToken).ConfigureAwait(false);
            }

            await MoveCollectionEntriesAsync(run, cancellationToken).ConfigureAwait(false);
            if (changed)
            {
                await _registry.SaveAsync(state with { Groups = groups }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _registry.Exit();
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GroupedStubsOf(IReadOnlyCollection<MergedCopy> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);

        var remote = copies.Where(copy => !copy.IsLocal)
            .Select(copy => (Server: Guid.TryParse(copy.ServerId, out var server) ? server : Guid.Empty, copy.ItemId))
            .ToList();
        var local = copies.Where(copy => copy.IsLocal)
            .Select(copy => Guid.TryParse(copy.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        bool Holds(StubEntry stub) => stub.Sources.Any(source => remote.Exists(copy => copy.Server == source.ServerId && string.Equals(copy.ItemId, source.ItemId, StringComparison.OrdinalIgnoreCase)));

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
            await DetachUngroupedAsync(state, state.Stubs.Where(stub => named.Contains(stub.Id)), new Run(root, false, _index, _library), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _registry.Exit();
        }
    }

    private static VersionLinkState StateOf(Video video) => new(
        video.Id,
        video.PrimaryVersionId,
        video.LinkedAlternateVersions.Select(child => child.ItemId).OfType<Guid>().ToList());

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
        var items = new Dictionary<Guid, Video>();
        var stubs = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var stub in group.StubIds.Select(_registry.Find).OfType<StubEntry>())
        {
            if (run.FindStub(stub) is { } video && items.TryAdd(video.Id, video))
            {
                stubs[stub.Id] = video.Id;
            }
        }

        // Only the first local copy found is linked: two local films are never merged (decision 20). A film an earlier
        // group links to stays out, and this group links its stubs among themselves (Q170).
        var locals = new List<Guid>();
        if (PrimaryLocal(group, run) is { } local && run.ClaimedLocals.Add(local.Id) && items.TryAdd(local.Id, local))
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
            run.Moves[moved] = items[primary.ItemId];
        }

        return primary.PrimaryStubId;
    }

    // Plans the links of some members against what Jellyfin has, aligns played if one joins, and saves the plan.
    private async Task<VersionLinkPlan> ApplyAsync(Guid primary, IReadOnlyList<Guid> alternates, IReadOnlyDictionary<Guid, Video> members, Run run, CancellationToken cancellationToken)
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
    private async Task DetachUngroupedAsync(StubState state, IEnumerable<StubEntry> stubs, Run run, CancellationToken cancellationToken)
    {
        var grouped = state.Groups.SelectMany(group => group.StubIds).ToHashSet(StringComparer.Ordinal);
        foreach (var stub in stubs.Where(stub => !grouped.Contains(stub.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (run.FindStub(stub) is { OwnerId: var owner } video && owner.Equals(Guid.Empty)
                    && (video.PrimaryVersionId is not null || video.LinkedAlternateVersions.Length > 0))
                {
                    await ApplyAsync(video.Id, [], new Dictionary<Guid, Video> { [video.Id] = video }, run, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type only: a message could hold a remote address.
                _logger.LogError("Cannot detach a version that left its group: {Error}", exception.GetType().Name);
            }
        }
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

    // The first local copy found, or the film the administrator merged it into as a version (§10 row 148).
    private Video? PrimaryLocal(VersionGroup group, Run run)
    {
        var local = group.LocalItemIds.Select(FindLocal).OfType<Video>().FirstOrDefault();
        var seen = new HashSet<Guid>();
        while (local is { PrimaryVersionId: { } above } && seen.Add(local.Id)
            && _library.GetItemById(above) is Video { OwnerId: var owner } film && owner.Equals(Guid.Empty) && !run.IsStub(film))
        {
            local = film;
        }

        return local;
    }

    // A file-based alternate of a local film stands for its owner, which Jellyfin shows.
    private Video? FindLocal(Guid id)
    {
        if (_library.GetItemById(id) is not Video video)
        {
            return null;
        }

        return !video.OwnerId.Equals(Guid.Empty) && _library.GetItemById(video.OwnerId) is Video owner ? owner : video;
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
                var target = child.ItemId is { } id && run.Moves.TryGetValue(id, out var primary) ? LinkedChild.Create(primary) : child;
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
        private IndexItemLookup? _lookup;
        private List<BoxSet>? _collections;

        // The primary that each alternate's collection entries move to.
        public Dictionary<Guid, Video> Moves { get; } = [];

        // The local films a group already links to in this run.
        public HashSet<Guid> ClaimedLocals { get; } = [];

        public bool Thorough => thorough;

        public bool IsStub(BaseItem item) => FederatedPaths.IsInside(root, item.Path);

        public Video? FindStub(StubEntry stub)
        {
            var path = FederatedPaths.Resolve(root, stub.RelativePath);
            Type kind = stub.Kind == StubKind.Episode ? typeof(Episode) : typeof(Movie);
            var video = library.GetItemById(library.GetNewItemId(path, kind)) as Video
                ?? library.GetItemById(library.GetNewItemId(path, typeof(Video))) as Video;
            return video ?? (thorough ? library.FindByPath(path, false) as Video : null);
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
}
