using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILibraryRenamer"/> as Jellyfin 12.4 renames a library from the dashboard. The old id is kept as retired,
/// since stock serves it until a restart (§10 row 86).
/// </summary>
public sealed class LibraryRenamer : ILibraryRenamer
{
    private static readonly PreferenceKind[] _accessLists =
    [
        PreferenceKind.EnabledFolders,
        PreferenceKind.BlockedMediaFolders,
        PreferenceKind.EnableContentDeletionFromFolders
    ];

    private static readonly PreferenceKind[] _homeLibraryLists = [PreferenceKind.LatestItemExcludes, PreferenceKind.GroupedFolders];

    private static readonly PreferenceKind[] _homeViewLists = [PreferenceKind.MyMediaExcludes, PreferenceKind.OrderedViews];

    private readonly IServerApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly ILibraryMonitor _monitor;
    private readonly IUserManager _users;
    private readonly ILibraryViewIds _views;
    private readonly RetiredLibraryIds _retired;
    private readonly ILogger<LibraryRenamer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRenamer"/> class.
    /// </summary>
    /// <param name="paths">The server paths, for the libraries folder.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="monitor">The library monitor.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="views">The view ids of the running server.</param>
    /// <param name="retired">The retired library ids.</param>
    /// <param name="logger">The logger.</param>
    public LibraryRenamer(
        IServerApplicationPaths paths,
        ILibraryManager library,
        ILibraryMonitor monitor,
        IUserManager users,
        ILibraryViewIds views,
        RetiredLibraryIds retired,
        ILogger<LibraryRenamer> logger)
    {
        _paths = paths;
        _library = library;
        _monitor = monitor;
        _users = users;
        _views = views;
        _retired = retired;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Guid?> RenameAsync(string name, Guid? itemId, CollectionType? type, string newName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        cancellationToken.ThrowIfCancellationRequested();

        var root = _paths.DefaultUserViewsPath;
        if (ChildOf(root, name) is not { } currentPath || ChildOf(root, newName) is not { } newPath || !Directory.Exists(currentPath))
        {
            return null;
        }

        var caseOnly = string.Equals(currentPath, newPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Directory.Exists(newPath))
        {
            return null;
        }

        var oldId = itemId ?? _library.GetNewItemId(currentPath, typeof(CollectionFolder));
        var newId = _library.GetNewItemId(newPath, typeof(CollectionFolder));
        _monitor.Stop();
        try
        {
            if (caseOnly)
            {
                var temporary = Path.Combine(root, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
                Directory.Move(currentPath, temporary);
                currentPath = temporary;
            }

            Directory.Move(currentPath, newPath);
            if (!oldId.Equals(newId))
            {
                _retired.Retire(oldId);
            }

            _retired.Restore(newId);
            await RewritePreferencesAsync(oldId, newId, name, newName, type).ConfigureAwait(false);
        }
        finally
        {
            CollectionFolder.OnCollectionFolderChange();
        }

        _logger.LogInformation("Renamed the library {Library} to {NewName}", name, newName);
        return newId;
    }

    // The path of a child of the libraries folder, or null for a name that would leave it.
    private static string? ChildOf(string root, string name)
    {
        var full = Path.GetFullPath(Path.Combine(root, name));
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal) ? full : null;
    }

    // Replaces the ids in place, keeping each value's form and dropping duplicates the replacement makes, as 12.4 does.
    private static bool ReplaceIds(User user, PreferenceKind kind, Dictionary<Guid, Guid> map)
    {
        var values = user.GetPreference(kind);
        var rewritten = new List<string>(values.Length);
        var seen = new HashSet<Guid>();
        var touched = false;
        foreach (var value in values)
        {
            if (!Guid.TryParse(value, out var parsed))
            {
                rewritten.Add(value);
                continue;
            }

            var replaced = map.TryGetValue(parsed, out var replacement) && !replacement.Equals(parsed);
            if (replaced)
            {
                parsed = replacement;
                touched = true;
            }

            if (!seen.Add(parsed))
            {
                touched = true;
                continue;
            }

            rewritten.Add(replaced ? parsed.ToString(value.Contains('-', StringComparison.Ordinal) ? "D" : "N", CultureInfo.InvariantCulture) : value);
        }

        if (touched)
        {
            user.SetPreference(kind, rewritten.ToArray());
        }

        return touched;
    }

    private async Task RewritePreferencesAsync(Guid oldId, Guid newId, string oldName, string newName, CollectionType? type)
    {
        var homeLists = _views.IsAvailable;
        var shadow = homeLists ? (Old: _views.ShadowViewId(oldName, oldId, type), New: _views.ShadowViewId(newName, newId, type)) : default;
        if (oldId.Equals(newId) && shadow.Old.Equals(shadow.New))
        {
            return;
        }

        foreach (var user in _users.GetUsers().ToList())
        {
            var libraries = new Dictionary<Guid, Guid> { [oldId] = newId };
            var changed = false;
            foreach (var kind in homeLists ? _accessLists.Concat(_homeLibraryLists) : _accessLists)
            {
                changed |= ReplaceIds(user, kind, libraries);
            }

            if (homeLists)
            {
                var views = new Dictionary<Guid, Guid>(libraries)
                {
                    [shadow.Old] = shadow.New,
                    [_views.NamedViewId(user, oldId, type)] = _views.NamedViewId(user, newId, type)
                };
                foreach (var kind in _homeViewLists)
                {
                    changed |= ReplaceIds(user, kind, views);
                }
            }

            if (changed)
            {
                await _users.UpdateUserAsync(user).ConfigureAwait(false);
            }
        }
    }
}
