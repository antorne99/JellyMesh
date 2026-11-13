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
using Jellyfin.Plugin.JellyMesh.Configuration;
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
    private readonly LibraryRenameJournal _journal;
    private readonly IPluginConfigurationStore _store;
    private readonly ILogger<LibraryRenamer> _logger;
    private readonly ILibraryChangeTracker? _changes;
    private readonly Action<string, string> _move;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRenamer"/> class.
    /// </summary>
    /// <param name="paths">The server paths, for the libraries folder.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="monitor">The library monitor.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="views">The view ids of the running server.</param>
    /// <param name="retired">The retired library ids.</param>
    /// <param name="journal">The renames whose user lists are not all rewritten yet.</param>
    /// <param name="store">The configuration store, whose registries name the libraries of the plugin.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="changes">The library change tracker, told of the new id so the page does not warn (Q53).</param>
    public LibraryRenamer(
        IServerApplicationPaths paths,
        ILibraryManager library,
        ILibraryMonitor monitor,
        IUserManager users,
        ILibraryViewIds views,
        RetiredLibraryIds retired,
        LibraryRenameJournal journal,
        IPluginConfigurationStore store,
        ILogger<LibraryRenamer> logger,
        ILibraryChangeTracker? changes = null)
        : this(paths, library, monitor, users, views, retired, journal, store, logger, changes, Directory.Move)
    {
    }

    // Takes how a folder is moved, so that tests can stop a rename between its steps.
    internal LibraryRenamer(
        IServerApplicationPaths paths,
        ILibraryManager library,
        ILibraryMonitor monitor,
        IUserManager users,
        ILibraryViewIds views,
        RetiredLibraryIds retired,
        LibraryRenameJournal journal,
        IPluginConfigurationStore store,
        ILogger<LibraryRenamer> logger,
        ILibraryChangeTracker? changes,
        Action<string, string> move)
    {
        _paths = paths;
        _library = library;
        _monitor = monitor;
        _users = users;
        _views = views;
        _retired = retired;
        _journal = journal;
        _store = store;
        _logger = logger;
        _changes = changes;
        _move = move;
    }

    /// <inheritdoc />
    public bool HasPending => _journal.HasPending;

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
        var record = new LibraryRenameRecord(name, newName, oldId, newId, type, caseOnly ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) : null);

        // Who blocks the old id blocks every id the folder takes on its way, and the record is saved, before it moves.
        await CloseAsync(record, root).ConfigureAwait(false);
        _journal.Add(record);
        _monitor.Stop();
        try
        {
            if (record.TemporaryName is { } temporaryName)
            {
                var temporary = Path.Combine(root, temporaryName);
                _move(currentPath, temporary);
                Retire(oldId, newId);
                currentPath = temporary;
            }

            _move(currentPath, newPath);
            Retire(oldId, newId);
            _changes?.Renamed(oldId, newId, newName);
            await FinishAsync(record, root).ConfigureAwait(false);
        }
        finally
        {
            CollectionFolder.OnCollectionFolderChange();
        }

        _logger.LogInformation("Renamed the library {Library} to {NewName}", LogText.Clean(name), LogText.Clean(newName));
        return newId;
    }

    /// <inheritdoc />
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        var root = _paths.DefaultUserViewsPath;
        foreach (var record in _journal.Pending())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ResumeAsync(record, root).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError("Cannot finish the rename of a library: {Error}", exception.GetType().Name);
            }
        }
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

    // The exact names in the libraries folder: a check by path cannot tell a change of case on some file systems.
    private static HashSet<string> FolderNames(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal)
        : [];

    private void Retire(Guid oldId, Guid newId)
    {
        if (!oldId.Equals(newId))
        {
            _retired.Retire(oldId);
        }

        _retired.Restore(newId);
    }

    // The ids the folder takes after the old one: the temporary one of a change of case, then the new one.
    private List<Guid> LaterIds(LibraryRenameRecord record, string root)
    {
        var ids = new List<Guid>();
        if (record.TemporaryName is { } temporary)
        {
            ids.Add(_library.GetNewItemId(Path.Combine(root, temporary), typeof(CollectionFolder)));
        }

        ids.Add(record.NewId);
        return ids.Where(id => !id.Equals(record.OldId)).Distinct().ToList();
    }

    // Adds the later ids to each list that blocks the old one. Any failure stops the rename before the folder moves.
    private async Task CloseAsync(LibraryRenameRecord record, string root)
    {
        var later = LaterIds(record, root);
        foreach (var user in _users.GetUsers().ToList())
        {
            var blocked = user.GetPreference(PreferenceKind.BlockedMediaFolders);
            if (!blocked.Any(value => Guid.TryParse(value, out var id) && id.Equals(record.OldId)))
            {
                continue;
            }

            var missing = later.Where(id => !blocked.Any(value => Guid.TryParse(value, out var held) && held.Equals(id))).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            user.SetPreference(PreferenceKind.BlockedMediaFolders, [.. blocked, .. missing.Select(id => id.ToString("N", CultureInfo.InvariantCulture))]);
            try
            {
                await _users.UpdateUserAsync(user).ConfigureAwait(false);
            }
            catch
            {
                user.SetPreference(PreferenceKind.BlockedMediaFolders, blocked);
                throw;
            }
        }
    }

    private async Task ResumeAsync(LibraryRenameRecord record, string root)
    {
        if (!IsBound(record, root))
        {
            _logger.LogError("A pending library rename does not match the library folders. It is set aside");
            _journal.SetAside(record);
            return;
        }

        var names = FolderNames(root);
        if (record.TemporaryName is { } temporary && names.Contains(temporary))
        {
            if (names.Contains(record.NewName))
            {
                _logger.LogWarning("Cannot finish the rename of the library {Library}: its new name is taken", LogText.Clean(record.OldName));
                return;
            }

            // Only the folder of the library moves, outside every watched path, so the monitor runs on.
            _move(Path.Combine(root, temporary), Path.Combine(root, record.NewName));
            CollectionFolder.OnCollectionFolderChange();
            _retired.Retire(_library.GetNewItemId(Path.Combine(root, temporary), typeof(CollectionFolder)));
            _retired.Restore(record.NewId);
            _changes?.Renamed(record.OldId, record.NewId, record.NewName);
            names = FolderNames(root);
        }

        if (names.Contains(record.OldName))
        {
            // The folder never moved, or a library was made again with the old name and so its id: lists stay (row 331).
            _journal.Remove(record);
            return;
        }

        await FinishAsync(record, root).ConfigureAwait(false);
    }

    // True when the names stay in the libraries folder, the new id is the new folder's and the old id the old folder's or
    // a library of the plugin's, so a record written by someone else moves and remaps nothing (row 329).
    private bool IsBound(LibraryRenameRecord record, string root)
    {
        try
        {
            if (ChildOf(root, record.OldName) is not { } oldPath
                || ChildOf(root, record.NewName) is not { } newPath
                || (record.TemporaryName is { } temporary && ChildOf(root, temporary) is null)
                || !_library.GetNewItemId(newPath, typeof(CollectionFolder)).Equals(record.NewId))
            {
                return false;
            }

            return _library.GetNewItemId(oldPath, typeof(CollectionFolder)).Equals(record.OldId)
                || _store.Read(config => config.Servers.SelectMany(server => server.AutomaticLibraryRegistry)
                    .Concat(config.RemovedServerLibraries.SelectMany(held => held.Libraries))
                    .Any(library => Guid.TryParse(library.ItemId, out var id) && id.Equals(record.OldId)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Drops the record once every user is rewritten. With no user listed, as early in a start, it waits.
    private async Task FinishAsync(LibraryRenameRecord record, string root)
    {
        if (await RewritePreferencesAsync(record, root).ConfigureAwait(false))
        {
            _journal.Remove(record);
        }
    }

    // Rewrites user by user: one that fails gets its lists back and is left for the next try, and the others go on.
    private async Task<bool> RewritePreferencesAsync(LibraryRenameRecord record, string root)
    {
        var homeLists = _views.IsAvailable;
        var shadow = homeLists
            ? (Old: _views.ShadowViewId(record.OldName, record.OldId, record.Type), New: _views.ShadowViewId(record.NewName, record.NewId, record.Type))
            : default;
        var users = _users.GetUsers().ToList();
        if (users.Count == 0)
        {
            return false;
        }

        var libraries = LaterIds(record, root).Prepend(record.OldId).Distinct().ToDictionary(id => id, _ => record.NewId);
        var done = true;
        PreferenceKind[] libraryLists = homeLists ? [.. _accessLists, .. _homeLibraryLists] : _accessLists;
        PreferenceKind[] allLists = homeLists ? [.. libraryLists, .. _homeViewLists] : libraryLists;
        foreach (var user in users)
        {
            var before = allLists.ToDictionary(kind => kind, user.GetPreference);
            try
            {
                var changed = false;
                foreach (var kind in libraryLists)
                {
                    changed |= ReplaceIds(user, kind, libraries);
                }

                if (homeLists)
                {
                    var views = new Dictionary<Guid, Guid>(libraries)
                    {
                        [shadow.Old] = shadow.New,
                        [_views.NamedViewId(user, record.OldId, record.Type)] = _views.NamedViewId(user, record.NewId, record.Type)
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                foreach (var (kind, values) in before)
                {
                    user.SetPreference(kind, values);
                }

                _logger.LogError("Cannot rewrite the library lists of a user after a rename: {Error}", exception.GetType().Name);
                done = false;
            }
        }

        return done;
    }
}
