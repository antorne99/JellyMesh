using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Clears the watch state of the stubs that covered one episode and now cover several: the state of one is never that of
/// more (§10 row 543).
/// </summary>
public sealed class WatchStateCleaner
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILogger<WatchStateCleaner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStateCleaner"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="logger">The logger.</param>
    public WatchStateCleaner(ILibraryManager library, IUserManager users, IUserDataManager userData, ILogger<WatchStateCleaner> logger)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _logger = logger;
    }

    /// <summary>
    /// Clears the state from before the mark of every user on the marked stubs. One that a split waits for, that has no item,
    /// or that cannot be cleared now, keeps its mark for the next call.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="state">The registry.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The registry without the marks of the cleared stubs.</returns>
    public StubState Clear(string root, StubState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var marked = state.Stubs.Where(stub => stub.StateOfOneUntil is not null).ToList();
        if (marked.Count == 0)
        {
            return state;
        }

        // No users may mean they cannot be read for now.
        var users = _users.GetUsers().ToList();
        if (users.Count == 0)
        {
            return state;
        }

        var waited = state.Splits.SelectMany(split => new[] { split.StubId, split.FromStubId }).ToHashSet(StringComparer.Ordinal);
        var cleared = marked
            .Where(stub => !waited.Contains(stub.Id) && TryClear(root, state, stub, users, cancellationToken))
            .Select(stub => stub.Id)
            .ToHashSet(StringComparer.Ordinal);
        return cleared.Count == 0
            ? state
            : state with { Stubs = state.Stubs.Select(stub => cleared.Contains(stub.Id) ? stub with { StateOfOneUntil = null } : stub).ToList() };
    }

    private static bool IsEmpty(UserItemData data) =>
        !data.Played && data.PlayCount == 0 && data.PlaybackPositionTicks == 0 && data.LastPlayedDate is null && !data.IsFavorite && data.Rating is null;

    // Also at the old paths of a pending server rename, which gives their state to the new one.
    private bool TryClear(string root, StubState state, StubEntry stub, List<User> users, CancellationToken cancellationToken)
    {
        var cleared = false;
        try
        {
            foreach (var entry in state.WithOldPaths([stub]))
            {
                if (_library.FindByPath(FederatedPaths.Resolve(root, entry.RelativePath), false) is { } item)
                {
                    // The instance Jellyfin caches: another would leave it the old rows.
                    ClearItem(_library.GetItemById(item.Id) ?? item, stub.StateOfOneUntil, users, cancellationToken);
                    cleared |= string.Equals(entry.RelativePath, stub.RelativePath, StringComparison.Ordinal);
                }
            }

            return cleared;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Cannot clear the watch state of a stub that now covers more episodes: {Error}", exception.GetType().Name);
            return false;
        }
    }

    // A row played since the mark is of the episodes the stub has now.
    private void ClearItem(BaseItem item, DateTime? until, List<User> users, CancellationToken cancellationToken)
    {
        foreach (var user in users)
        {
            if (_userData.GetUserData(user, item) is not { } data || IsEmpty(data) || data.LastPlayedDate > until)
            {
                continue;
            }

            data.Played = false;
            data.PlayCount = 0;
            data.PlaybackPositionTicks = 0;
            data.LastPlayedDate = null;
            data.IsFavorite = false;
            data.Rating = null;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, cancellationToken);
        }
    }
}
