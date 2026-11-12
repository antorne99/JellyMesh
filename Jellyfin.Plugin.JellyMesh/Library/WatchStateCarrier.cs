using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IWatchStateCarrier"/> over the user data of Jellyfin. Each user's state goes only to that user's rows on the
/// items they see, with the resume position on the version of the server that last served them (D15, decision 149). It
/// changes copies of the rows, so a row it could not save stays as Jellyfin has it.
/// </summary>
public sealed class WatchStateCarrier : IWatchStateCarrier
{
    private readonly StubRegistry _registry;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILastServedSourceStore _lastServed;
    private readonly IWatchStateCarryLog _log;
    private readonly ILogger<WatchStateCarrier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStateCarrier"/> class.
    /// </summary>
    /// <param name="registry">The stub registry, for the stubs and their groups.</param>
    /// <param name="store">The configuration, for the federated folder.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="lastServed">The last server that served each stub to each user.</param>
    /// <param name="log">The carries done.</param>
    /// <param name="logger">The logger.</param>
    public WatchStateCarrier(
        StubRegistry registry,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        ILastServedSourceStore lastServed,
        IWatchStateCarryLog log,
        ILogger<WatchStateCarrier> logger)
    {
        _registry = registry;
        _store = store;
        _library = library;
        _users = users;
        _userData = userData;
        _lastServed = lastServed;
        _log = log;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WatchStateCarryReport> CarryAsync(IReadOnlyList<WatchStateMove> moves, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moves);

        // Without the record every carry would look like the first and put back what users took off (decision 176).
        if (!_log.EnsureLoaded())
        {
            _logger.LogWarning("The record of the watch state carries cannot be read for now: the watch state of {Count} titles is not carried", moves.Count);
            return new WatchStateCarryReport(moves.Select(move => Outcome(move, WatchStateMoveStatus.Failed)).ToList());
        }

        var run = new Run(_store.Read(config => config.FederatedFolderPath) ?? string.Empty, _registry.State, _users.GetUsers().ToList());

        // The film the admin merged a local copy into heads its group in place of that copy (Q220).
        var heads = moves.SelectMany(move => move.Targets).Where(target => target.ServerId is null && !run.GroupOfLocal.ContainsKey(target.ItemId)).Select(target => target.ItemId).ToHashSet();
        foreach (var group in run.State.Groups.Where(group => heads.Count > 0 && group.LocalItemIds.Count > 0))
        {
            if (LocalVersionHead.Of(group.LocalItemIds, _library, item => FederatedPaths.IsInside(run.Root, item.Path)) is { } head && heads.Contains(head.Id))
            {
                run.GroupOfLocal.TryAdd(head.Id, group.Id);
            }
        }

        var outcomes = new List<WatchStateMoveOutcome>(moves.Count);
        var recorded = new Dictionary<int, List<Guid>>();
        try
        {
            foreach (var move in moves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var done = new List<Guid>();
                WatchStateMoveOutcome outcome;
                try
                {
                    outcome = CarryMove(move, run, done, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The type only: a message could hold a path.
                    _logger.LogError("Cannot carry the watch state of a title: {Error}", exception.GetType().Name);
                    outcome = new WatchStateMoveOutcome(move, WatchStateMoveStatus.Failed, []);
                }

                if (done.Count > 0)
                {
                    recorded[outcomes.Count] = done;
                }

                outcomes.Add(outcome);
            }
        }
        catch (OperationCanceledException)
        {
            // What was saved stays recorded, so that the next run does not carry favorites and ratings again.
            await _log.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        try
        {
            Prune(run);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path. The records of this run are saved all the same.
            _logger.LogError("Cannot clean up the record of the watch state carries: {Error}", exception.GetType().Name);
        }

        // A move done may rest on a record from an earlier run that is still not on disk, so none counts as done.
        // Rows are saved by now, so a late cancellation must not leave their records only in memory.
        IReadOnlyList<Guid> forgettable = [];
        if (!await _log.SaveAsync(CancellationToken.None).ConfigureAwait(false))
        {
            for (var index = 0; index < outcomes.Count; index++)
            {
                var outcome = outcomes[index];
                if (outcome.Status is not (WatchStateMoveStatus.Carried or WatchStateMoveStatus.UsersLeft))
                {
                    continue;
                }

                var users = recorded.GetValueOrDefault(index) ?? [];
                var skipped = outcome.SkippedUsers.Concat(users.Select(user => new WatchStateUserSkip(user, WatchStateUserSkipReason.NotRecorded)));
                outcomes[index] = outcome with { Status = WatchStateMoveStatus.Failed, SkippedUsers = skipped.ToList() };
            }
        }
        else if (run.Users.Count > 0)
        {
            // With no users nothing was carried for anyone, so no old item may forget its records (decision 203).
            forgettable = ForgettableOf(outcomes);
        }

        var report = new WatchStateCarryReport(outcomes) { ForgettableOldItemIds = forgettable };
        if (report.Skipped > 0)
        {
            _logger.LogWarning("Watch state carried for {Carried} titles, {Skipped} left for a later run", report.Carried, report.Skipped);
        }

        return report;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ForgetRemovedAsync(IReadOnlyCollection<Guid> oldItemIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(oldItemIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (oldItemIds.Count == 0)
        {
            return [];
        }

        // Without the record a later carry would look like the first (decision 176).
        if (!_log.EnsureLoaded())
        {
            _logger.LogWarning("The record of the watch state carries cannot be read for now: the carries of {Count} removed titles are not forgotten", oldItemIds.Count);
            return [];
        }

        // No users may mean they cannot be read for now, as in Prune.
        if (!_users.GetUsers().Any())
        {
            return [];
        }

        // Only the admin removes the old item, so the caller is not trusted: one still in the library keeps its records
        // until a later call or Prune finds it gone (decision 204).
        var asked = oldItemIds.ToHashSet();
        var gone = asked.Where(IsGone).ToList();
        if (gone.Count < asked.Count)
        {
            _logger.LogInformation("The carries of {Count} titles are kept while they are still in the library", asked.Count - gone.Count);
        }

        return await ForgetOldAsync(gone).ConfigureAwait(false) ? gone : [];
    }

    private static WatchStateMoveOutcome Outcome(WatchStateMove move, WatchStateMoveStatus status) => new(move, status, []);

    private static bool ChangesFavoriteOrRating(UserItemData? before, UserItemData after) =>
        before is null || before.IsFavorite != after.IsFavorite || before.Rating != after.Rating;

    private static bool HasAnything(UserItemData data) =>
        data.Played || data.PlayCount > 0 || data.PlaybackPositionTicks > 0 || data.LastPlayedDate is not null || data.IsFavorite || data.Rating is not null;

    // The versions of each title, or the status that stops the move: a version not linked, or one that is not allowed.
    private static (List<List<(Video Item, Guid? ServerId)>>? Titles, WatchStateMoveStatus Status) TitlesOf(
        IReadOnlyList<(BaseItem Item, Guid? ServerId)> targets,
        Run run,
        StubRegistry registry,
        WatchStateMove move)
    {
        if (targets.Count(target => target.ServerId is null) > 1)
        {
            return (null, WatchStateMoveStatus.Refused);
        }

        var byGroup = new Dictionary<string, List<(Video Item, Guid? ServerId)>>(StringComparer.Ordinal);
        foreach (var (item, serverId) in targets)
        {
            if (item is not Video video)
            {
                return (null, WatchStateMoveStatus.Refused);
            }

            string? group;
            if (serverId is { } server)
            {
                if (registry.FindByPath(video.Path) is not { } stub || stub.ServerId != server)
                {
                    return (null, WatchStateMoveStatus.Refused);
                }

                group = run.GroupOfStub.GetValueOrDefault(stub.Id);
            }
            else
            {
                // The local copy is a version only as the primary of its group (Q43, decision 148).
                group = FederatedPaths.RelativeTo(run.Root, video.Path ?? string.Empty) is null
                    ? run.GroupOfLocal.GetValueOrDefault(video.Id)
                    : null;
                if (group is null)
                {
                    return (null, WatchStateMoveStatus.Refused);
                }
            }

            // A stub in no group stands alone (D3).
            var key = group is null ? "item:" + video.Id.ToString("N") : "group:" + group;
            if (!byGroup.TryGetValue(key, out var title))
            {
                byGroup[key] = title = [];
            }

            title.Add((video, serverId));
        }

        foreach (var title in byGroup.Values)
        {
            if (title.Any(version => version.ServerId is null && version.Item.PrimaryVersionId is not null))
            {
                return (null, WatchStateMoveStatus.Refused);
            }

            if (PrimaryOf(title, move) is null)
            {
                return (null, WatchStateMoveStatus.NotLinked);
            }
        }

        return (byGroup.Values.ToList(), WatchStateMoveStatus.Carried);
    }

    // The new item of a renamed stub, owned by the old item until its file goes, is the primary of its own title (Q38).
    private static Guid? PrimaryOf(List<(Video Item, Guid? ServerId)> title, WatchStateMove move) =>
        move.RemovesOldItem && title is [{ ServerId: not null, Item: var only }] && only.OwnerId == move.FromItemId && only.PrimaryVersionId == move.FromItemId
            ? only.Id
            : PrimaryOf(title.Select(version => version.Item).ToList());

    // The primary as the link left it: the only version that names none, named by every other and holding each of them.
    private static Guid? PrimaryOf(IReadOnlyList<Video> versions)
    {
        var heads = versions.Where(version => version.PrimaryVersionId is null).ToList();
        if (heads.Count != 1)
        {
            return null;
        }

        var primary = heads[0];
        var linked = primary.LinkedAlternateVersions.Select(child => child.ItemId).OfType<Guid>().ToHashSet();
        return versions.All(version => version == primary || (version.PrimaryVersionId == primary.Id && linked.Contains(version.Id))) ? primary.Id : null;
    }

    private WatchStateMoveOutcome CarryMove(WatchStateMove move, Run run, List<Guid> recorded, CancellationToken cancellationToken)
    {
        if (run.Root.Length == 0)
        {
            return Outcome(move, WatchStateMoveStatus.Refused);
        }

        if (_library.GetItemById(move.FromItemId) is not { } from)
        {
            return Outcome(move, WatchStateMoveStatus.NotFound);
        }

        var isSeries = move.Kind == WatchStateMoveKind.Series;
        if ((isSeries ? from is not Series : from is not Video)
            || !string.Equals(FederatedPaths.RelativeTo(run.Root, from.Path ?? string.Empty), move.FromPath, StringComparison.Ordinal))
        {
            return Outcome(move, WatchStateMoveStatus.Refused);
        }

        var targets = new List<(BaseItem Item, Guid? ServerId)>();

        // A merged version takes nothing from itself.
        foreach (var target in move.Targets.Where(target => target.ItemId != move.FromItemId).DistinctBy(target => target.ItemId))
        {
            if (_library.GetItemById(target.ItemId) is not { } item)
            {
                return Outcome(move, WatchStateMoveStatus.NotFound);
            }

            targets.Add((item, target.ServerId));
        }

        if (targets.Count == 0)
        {
            return Outcome(move, WatchStateMoveStatus.Refused);
        }

        if (isSeries)
        {
            return IsSeriesStubs(targets, run)
                ? CarryUsers(move, run, from, recorded, (user, old, mode) => SeriesRows(user, old, mode, targets), cancellationToken)
                : Outcome(move, WatchStateMoveStatus.Refused);
        }

        var (titles, status) = TitlesOf(targets, run, _registry, move);
        return titles is null
            ? Outcome(move, status)
            : CarryUsers(move, run, from, recorded, (user, old, mode) => TitleRows(user, old, mode, move, titles), cancellationToken);
    }

    private static bool IsSeriesStubs(IReadOnlyList<(BaseItem Item, Guid? ServerId)> targets, Run run) => targets.All(target =>
        target.Item is Series
        && target.ServerId is { } server
        && FederatedPaths.RelativeTo(run.Root, target.Item.Path ?? string.Empty) is { } relative
        && run.State.Series.Any(series => series.ServerId == server && string.Equals(series.RelativePath, relative, StringComparison.Ordinal)));

    // Carries the move for each user; rows gives the changed rows, or why the user is left.
    private WatchStateMoveOutcome CarryUsers(
        WatchStateMove move,
        Run run,
        BaseItem from,
        List<Guid> recorded,
        Func<User, UserItemData, WatchStateCarryMode, (List<(BaseItem Item, UserItemData Data)>? Rows, WatchStateUserSkipReason Reason)> rows,
        CancellationToken cancellationToken)
    {
        var skipped = new List<WatchStateUserSkip>();
        foreach (var user in run.Users)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_userData.GetUserData(user, from) is not { } old || ModeOf(user, from, old, move.Kind) is not { } mode)
            {
                continue;
            }

            var (changed, reason) = rows(user, old, mode);
            if (changed is null)
            {
                skipped.Add(new WatchStateUserSkip(user.Id, reason));
                continue;
            }

            // A favorite or rating already written is not written again, even if a later row fails or the run is cancelled:
            // a later run carries only the watch (decision 155).
            if (!Save(user, changed, () => RecordFavorite(user, from, mode), cancellationToken))
            {
                skipped.Add(new WatchStateUserSkip(user.Id, WatchStateUserSkipReason.NotSaved));
                continue;
            }

            _log.Record(user.Id, from.Id, old.LastPlayedDate);
            recorded.Add(user.Id);
        }

        return new WatchStateMoveOutcome(move, skipped.Count == 0 ? WatchStateMoveStatus.Carried : WatchStateMoveStatus.UsersLeft, skipped);
    }

    // Records a first carry with no date once its favorite or rating is saved, so it is not carried again (decision 155).
    private void RecordFavorite(User user, BaseItem from, WatchStateCarryMode mode)
    {
        if (mode == WatchStateCarryMode.First)
        {
            _log.Record(user.Id, from.Id, null);
        }
    }

    // Null when there is nothing to carry: an empty row, or a carry done with no newer watch since (decision 155).
    private WatchStateCarryMode? ModeOf(User user, BaseItem from, UserItemData old, WatchStateMoveKind kind)
    {
        if (!_log.TryFind(user.Id, from.Id, out var carried))
        {
            return HasAnything(old) ? WatchStateCarryMode.First : null;
        }

        // A series holds only favorite and rating, which go once.
        return kind == WatchStateMoveKind.Title && old.LastPlayedDate is { } watched && (carried is null || watched > carried)
            ? WatchStateCarryMode.Repeat
            : null;
    }

    private (List<(BaseItem Item, UserItemData Data)>? Rows, WatchStateUserSkipReason Reason) TitleRows(
        User user,
        UserItemData old,
        WatchStateCarryMode mode,
        WatchStateMove move,
        List<List<(Video Item, Guid? ServerId)>> titles)
    {
        var lastServer = _lastServed.Find(user.Id, move.FromStubId);
        var changed = new List<(BaseItem Item, UserItemData Data)>();
        var seen = false;
        foreach (var title in titles)
        {
            var versions = new List<(BaseItem Item, WatchStateVersion Version)>(title.Count);
            var primary = PrimaryOf(title, move);
            foreach (var (item, serverId) in title)
            {
                if (_userData.GetUserData(user, item) is not { } data)
                {
                    return (null, WatchStateUserSkipReason.NoUserData);
                }

                var visible = _library.GetItemById<BaseItem>(item.Id, user) is not null;
                versions.Add((item, new WatchStateVersion(serverId, item.Id == primary, WatchStateTransfer.Copy(data), visible)));
            }

            // Each title the user sees takes the state; stubs never linked are titles of their own (D3, decision 151).
            if (!versions.Any(version => version.Version.IsVisible))
            {
                continue;
            }

            seen = true;
            var rows = WatchStateTransfer.Carry(old, lastServer, move.Servers, versions.Select(version => version.Version).ToList(), mode, move.CarriesPosition)
                .ToHashSet(ReferenceEqualityComparer.Instance);
            changed.AddRange(versions.Where(version => rows.Contains(version.Version.Data)).Select(version => (version.Item, version.Version.Data)));
        }

        return seen ? (changed, default) : (null, WatchStateUserSkipReason.NoVisibleVersion);
    }

    private (List<(BaseItem Item, UserItemData Data)>? Rows, WatchStateUserSkipReason Reason) SeriesRows(
        User user,
        UserItemData old,
        WatchStateCarryMode mode,
        IReadOnlyList<(BaseItem Item, Guid? ServerId)> targets)
    {
        var visible = new List<(BaseItem Item, UserItemData Data)>(targets.Count);
        foreach (var (item, _) in targets.Where(target => _library.GetItemById<BaseItem>(target.Item.Id, user) is not null))
        {
            if (_userData.GetUserData(user, item) is not { } data)
            {
                return (null, WatchStateUserSkipReason.NoUserData);
            }

            visible.Add((item, WatchStateTransfer.Copy(data)));
        }

        if (visible.Count == 0)
        {
            return (null, WatchStateUserSkipReason.NoVisibleVersion);
        }

        var rows = mode == WatchStateCarryMode.First
            ? WatchStateTransfer.CarrySeries(old, visible.Select(series => series.Data).ToList()).ToHashSet(ReferenceEqualityComparer.Instance)
            : [];
        return (visible.Where(series => rows.Contains(series.Data)).ToList(), default);
    }

    // Saves the rows with a new favorite or rating first, calling favoriteSaved as soon as one of those is saved.
    private bool Save(User user, List<(BaseItem Item, UserItemData Data)> rows, Action favoriteSaved, CancellationToken cancellationToken)
    {
        var ordered = rows
            .Select(row => (row.Item, row.Data, Favorite: ChangesFavoriteOrRating(_userData.GetUserData(user, row.Item), row.Data)))
            .OrderByDescending(row => row.Favorite)
            .ToList();
        foreach (var (item, data, favorite) in ordered)
        {
            try
            {
                _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type only: a message could hold a path.
                _logger.LogError("Cannot save the watch state carried to a title: {Error}", exception.GetType().Name);
                return false;
            }

            if (favorite)
            {
                favoriteSaved();
            }
        }

        return true;
    }

    // Drops the records of users who are gone and of old items no longer in the library (decision 165).
    private void Prune(Run run)
    {
        var users = run.Users.Select(user => user.Id).ToHashSet();
        var exists = new Dictionary<Guid, bool>();
        _log.Forget((userId, itemId) =>
        {
            if (users.Count > 0 && !users.Contains(userId))
            {
                return true;
            }

            if (!exists.TryGetValue(itemId, out var found))
            {
                exists[itemId] = found = _library.GetItemById(itemId) is not null;
            }

            return !found;
        });
    }

    // The old items whose moves all remove them and are done for every user (decisions 179, 187).
    private static List<Guid> ForgettableOf(List<WatchStateMoveOutcome> outcomes) => outcomes
        .GroupBy(outcome => outcome.Move.FromItemId)
        .Where(moves => moves.All(outcome => outcome.Move.RemovesOldItem && outcome.Status == WatchStateMoveStatus.Carried))
        .Select(moves => moves.Key)
        .ToList();

    // Gone as Prune and the carry see it: Jellyfin no longer has the item. A lookup that fails counts as still there.
    private bool IsGone(Guid itemId)
    {
        try
        {
            return _library.GetItemById(itemId) is null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot look up a removed title before forgetting its carries: {Error}", exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> ForgetOldAsync(IReadOnlyCollection<Guid> oldItemIds)
    {
        var removed = oldItemIds.ToHashSet();
        if (removed.Count == 0 || _log.Forget((_, itemId) => removed.Contains(itemId)) == 0)
        {
            return true;
        }

        // A late cancellation must not leave the log forgotten in memory and kept on disk.
        if (!await _log.SaveAsync(CancellationToken.None).ConfigureAwait(false))
        {
            _logger.LogWarning("Cannot save the record of the watch state carries after forgetting {Count} removed titles", removed.Count);
            return false;
        }

        return true;
    }

    // What a run reads once, with the group of each stub and local copy.
    private sealed class Run(string root, StubState state, List<User> users)
    {
        public string Root { get; } = root;

        public StubState State { get; } = state;

        public List<User> Users { get; } = users;

        public Dictionary<string, string> GroupOfStub { get; } = state.Groups
            .SelectMany(group => group.StubIds.Select(stubId => (StubId: stubId, group.Id)))
            .DistinctBy(entry => entry.StubId, StringComparer.Ordinal)
            .ToDictionary(entry => entry.StubId, entry => entry.Id, StringComparer.Ordinal);

        public Dictionary<Guid, string> GroupOfLocal { get; } = state.Groups
            .SelectMany(group => group.LocalItemIds.Select(itemId => (ItemId: itemId, group.Id)))
            .DistinctBy(entry => entry.ItemId)
            .ToDictionary(entry => entry.ItemId, entry => entry.Id);
    }
}
