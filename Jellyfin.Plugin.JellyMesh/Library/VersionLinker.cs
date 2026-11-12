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
            var groups = new List<VersionGroup>(state.Groups.Count);
            var changed = false;
            foreach (var group in state.Groups)
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
                groups.Add(group with { PrimaryStubId = recorded });
            }

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

        // Only the first local copy found is linked: two local films are never merged (decision 20).
        var locals = new List<Guid>();
        if (PrimaryLocal(group, run) is { } local && items.TryAdd(local.Id, local))
        {
            locals.Add(local.Id);
        }

        if (items.Count < 2)
        {
            return group.PrimaryStubId;
        }

        var primary = VersionLinkPlanner.ChoosePrimary(locals, stubs, group.PrimaryStubId, candidates => run.Best(candidates, _registry));
        var alternates = items.Keys.Where(id => id != primary.ItemId).ToList();
        var plan = await ApplyAsync(primary.ItemId, alternates, items, run, cancellationToken).ConfigureAwait(false);

        var moved = plan.Joined.Where(id => id != primary.ItemId).ToList();
        if (moved.Count > 0)
        {
            await MoveCollectionEntriesAsync(moved, items[primary.ItemId], run, cancellationToken).ConfigureAwait(false);
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
        if (plan.Joined.Count > 0)
        {
            AlignPlayed(members.Values.ToList(), cancellationToken);
        }

        foreach (var update in plan.Updates)
        {
            await SaveLinksAsync(known[update.ItemId], update, cancellationToken).ConfigureAwait(false);
        }

        return plan;
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

    // Collections follow the primary as MergeVersions does; playlists keep the version that was added (Q79).
    private async Task MoveCollectionEntriesAsync(IReadOnlyList<Guid> moved, Video primary, Run run, CancellationToken cancellationToken)
    {
        var from = moved.ToHashSet();
        foreach (var collection in run.Collections().Where(collection => collection.LinkedChildren.Any(child => child.ItemId is { } id && from.Contains(id))))
        {
            var seen = new HashSet<Guid>();
            var children = new List<LinkedChild>(collection.LinkedChildren.Length);
            foreach (var child in collection.LinkedChildren)
            {
                var target = child.ItemId is { } id && from.Contains(id) ? LinkedChild.Create(primary) : child;
                if (target.ItemId is not { } targetId || seen.Add(targetId))
                {
                    children.Add(target);
                }
            }

            collection.LinkedChildren = children.ToArray();
            await _updater.SaveAsync(collection, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    // What one run looks up once: the quality of the stubs and the collections.
    private sealed class Run(string root, bool thorough, IFederationIndex index, ILibraryManager library)
    {
        private IndexItemLookup? _lookup;
        private List<BoxSet>? _collections;

        public bool IsStub(BaseItem item) => item.Path is { } path && FederatedPaths.RelativeTo(root, path) is not null;

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
            var byStub = new List<(string StubId, RemoteSource Source)>();
            foreach (var stubId in stubIds)
            {
                var source = registry.Find(stubId)?.Sources[0];
                var version = source is null
                    ? null
                    : _lookup.Find(source.ServerId, source.ItemId)?.Versions.FirstOrDefault(v => string.Equals(v.MediaSourceId, source.MediaSourceId, StringComparison.Ordinal));
                var quality = version is null ? new SourceQuality(null, false, null, null) { IsRangeKnown = false } : CatalogItemMapper.QualityOf(version);

                // The stub ID stands for the item, so that two stubs never count as one source.
                byStub.Add((stubId, new RemoteSource(source?.ServerId.ToString("N") ?? string.Empty, stubId, source?.MediaSourceId ?? string.Empty, quality)));
            }

            var best = QualityRanking.Rank(byStub.Select(entry => entry.Source))[0];
            return byStub.First(entry => entry.Source == best).StubId;
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
