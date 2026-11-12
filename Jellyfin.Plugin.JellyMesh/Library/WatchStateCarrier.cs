using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IWatchStateCarrier"/> over the user data of Jellyfin. Each user's state goes only to that user's rows, with
/// the resume position on the version of the server that last served them (D15).
/// </summary>
public sealed class WatchStateCarrier : IWatchStateCarrier
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILastServedSourceStore _lastServed;
    private readonly ILogger<WatchStateCarrier> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchStateCarrier"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="lastServed">The last server that served each stub to each user.</param>
    /// <param name="logger">The logger.</param>
    public WatchStateCarrier(ILibraryManager library, IUserManager users, IUserDataManager userData, ILastServedSourceStore lastServed, ILogger<WatchStateCarrier> logger)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _lastServed = lastServed;
        _logger = logger;
    }

    /// <inheritdoc />
    public WatchStateCarryReport Carry(IReadOnlyList<WatchStateMove> moves, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(moves);

        var users = _users.GetUsers().ToList();
        var (carried, skipped) = (0, 0);
        foreach (var move in moves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (CarryMove(move, users, cancellationToken))
                {
                    carried++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type only: a message could hold a path.
                _logger.LogError("Cannot carry the watch state of a title: {Error}", exception.GetType().Name);
                skipped++;
            }
        }

        if (skipped > 0)
        {
            _logger.LogWarning("Watch state carried for {Carried} titles, {Skipped} left for a later run", carried, skipped);
        }

        return new WatchStateCarryReport(carried, skipped);
    }

    private bool CarryMove(WatchStateMove move, List<User> users, CancellationToken cancellationToken)
    {
        if (_library.GetItemById(move.FromItemId) is not { } from)
        {
            return false;
        }

        var versions = new List<(BaseItem Item, Guid? ServerId)>(move.Versions.Count);
        foreach (var target in move.Versions)
        {
            if (_library.GetItemById(target.ItemId) is not { } item)
            {
                return false;
            }

            versions.Add((item, target.ServerId));
        }

        if (PrimaryOf(versions.Select(version => version.Item).ToList()) is not { } primary)
        {
            return false;
        }

        foreach (var user in users)
        {
            if (_userData.GetUserData(user, from) is not { } old)
            {
                continue;
            }

            var rows = new List<(BaseItem Item, WatchStateVersion Version)>(versions.Count);
            foreach (var (item, serverId) in versions)
            {
                if (_userData.GetUserData(user, item) is { } data)
                {
                    rows.Add((item, new WatchStateVersion(serverId, item.Id == primary, data)));
                }
            }

            // Jellyfin gives no row only to an item with no user data key, which cannot hold watch state.
            if (rows.Count < versions.Count)
            {
                continue;
            }

            var changed = WatchStateTransfer.Carry(old, _lastServed.Find(user.Id, move.FromStubId), move.Servers, rows.Select(row => row.Version).ToList())
                .ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var (item, version) in rows.Where(row => changed.Contains(row.Version.Data)))
            {
                _userData.SaveUserData(user, item, version.Data, UserDataSaveReason.Import, cancellationToken);
            }
        }

        return true;
    }

    // The primary as the link left it on the items, which follows Q43; a lone version is its own primary.
    private static Guid? PrimaryOf(IReadOnlyList<BaseItem> versions)
    {
        if (versions.Count == 1)
        {
            return versions[0].Id;
        }

        var named = versions.OfType<Video>().Select(video => video.PrimaryVersionId).OfType<Guid>().Distinct().ToList();
        return named.Count == 1 && versions.Any(version => version.Id == named[0]) ? named[0] : null;
    }
}
