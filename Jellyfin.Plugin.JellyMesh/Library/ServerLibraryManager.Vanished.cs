using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

// What the remote libraries that vanished leave behind, once their titles are gone (§10 row 436).
public sealed partial class ServerLibraryManager
{
    // Removes the libraries and folders of the vanished remote libraries whose titles the registry no longer holds:
    // own libraries holding only their folders are deleted, the others lose those folders, then the folders go.
    private async Task RemoveVanishedAsync(CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        var vanished = _store.Read(config => config.Servers
            .Select(server => (server.Id, Ids: server.VanishedLibraries.Select(library => library.Id)
                .Where(id => FederatedPaths.IsLibraryId(id) && !server.ImportedLibraryIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .Where(server => server.Ids.Count > 0)
            .ToList());
        if (root.Length == 0 || vanished.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var (serverId, ids) in vanished)
        {
            // A library with no id yet may be a renamed one, so nothing goes until the next check (decision 213).
            var gone = ids.Where(id => TitlesGone(serverId, id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (gone.Count > 0 && ReadFolders() is { } folders && folders.All(folder => !string.IsNullOrEmpty(folder.ItemId)))
            {
                changed |= await RemoveVanishedPathsAsync(serverId, gone, root, folders, cancellationToken).ConfigureAwait(false);
                DeleteVanishedFolders(serverId, gone, root);
            }
        }

        if (changed)
        {
            await QueueScanAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // A location in the folder of one of the given remote libraries of the server, or in that folder itself.
    private static bool IsVanishedFolder(string location, string root, Guid serverId, IReadOnlySet<string> ids) =>
        FederatedPaths.RelativeTo(root, Path.TrimEndingDirectorySeparator(location))?.Split(Path.DirectorySeparatorChar) is [var server, var library, ..]
        && Guid.TryParseExact(server, "D", out var id)
        && id == serverId
        && ids.Contains(library);

    // Known only once the registry is read: no stub or series of the server is left in a type folder of the remote library.
    private bool TitlesGone(Guid serverId, string libraryId)
    {
        var folder = FederatedPaths.LibraryFolder(serverId, libraryId) + Path.DirectorySeparatorChar;
        return TypeFolder.Written.All(type => _stubs.TypeFoldersOf(serverId, type) is { } held
            && !held.Any(path => (path + Path.DirectorySeparatorChar).StartsWith(folder, StringComparison.OrdinalIgnoreCase)));
    }

    // Deletes an own library that holds only vanished folders; any other library of the registry, adopted ones too,
    // only loses those folders and keeps its name (D22). Libraries out of the registry are the admin's and stay as they are.
    private async Task<bool> RemoveVanishedPathsAsync(Guid serverId, IReadOnlySet<string> gone, string root, List<VirtualFolderInfo> folders, CancellationToken cancellationToken)
    {
        var changed = false;
        var records = _store.Read(config => Find(config, serverId)?.AutomaticLibraryRegistry.Select(Copy).ToList() ?? []);
        foreach (var record in records)
        {
            if (FindRecorded(record, folders) is not { } folder || !Holds(folder, record, root, serverId))
            {
                continue;
            }

            var paths = folder.Locations.Where(location => IsVanishedFolder(location, root, serverId, gone)).ToList();
            if (paths.Count == 0)
            {
                continue;
            }

            if (!record.Adopted && paths.Count == folder.Locations.Length)
            {
                changed |= await DeleteVanishedLibraryAsync(serverId, record, gone, root, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var path in paths)
            {
                var outcome = _paths.Remove(root, folder.Name, path);
                changed |= outcome == LibraryPathOutcome.Done;
                if (outcome is not (LibraryPathOutcome.Done or LibraryPathOutcome.Unchanged or LibraryPathOutcome.NoLibrary))
                {
                    _logger.LogWarning("Cannot remove the folder of a vanished remote library from the library {Library}: {Outcome}", LogText.Clean(folder.Name), outcome);
                }
            }
        }

        return changed;
    }

    // Deletes the library, then forgets its record and that it was made, so a library imported later can have it again.
    // Checked again on a fresh read after the scans, so that the name is the current one and a changed library is left alone.
    private async Task<bool> DeleteVanishedLibraryAsync(Guid serverId, AutomaticLibraryConfig record, IReadOnlySet<string> gone, string root, CancellationToken cancellationToken)
    {
        await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
        if (ReadFolders() is not { } folders
            || record.ItemId.Length == 0
            || folders.FirstOrDefault(found => string.Equals(found.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase)) is not { } folder
            || record.Adopted
            || !Holds(folder, record, root, serverId)
            || !folder.Locations.All(location => IsVanishedFolder(location, root, serverId, gone)))
        {
            return false;
        }

        var id = Guid.TryParse(record.ItemId, out var found) ? found : (Guid?)null;
        if (!await RemoveFromUsersAsync(folder.Name, id, PreferenceKind.EnabledFolders, cancellationToken).ConfigureAwait(false)
            || !await RemoveFromUsersAsync(folder.Name, id, PreferenceKind.EnableContentDeletionFromFolders, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await _libraries.RemoveVirtualFolder(folder.Name, false).ConfigureAwait(false);
            _sharedValidationOwed = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot delete the library {Library} of a vanished remote library: {Error}{Detail:l}", LogText.Clean(folder.Name), exception.GetType().Name, ErrorText.Of(exception));
            return false;
        }

        var key = KeyOf(record);
        _store.Update(config =>
        {
            if (Find(config, serverId) is not { } server)
            {
                return;
            }

            if (server.AutomaticLibraryRegistry.FirstOrDefault(kept => kept.Kind == record.Kind && kept.RemoteLibraryId == record.RemoteLibraryId && string.Equals(kept.Name, record.Name, StringComparison.Ordinal)) is { } kept)
            {
                server.AutomaticLibraryRegistry.Remove(kept);
            }

            foreach (var made in server.CreatedLibraryKeys.Where(made => string.Equals(made, key, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                server.CreatedLibraryKeys.Remove(made);
            }
        });
        _logger.LogInformation("Deleted the library {Library}: its remote library is gone", LogText.Clean(folder.Name));
        return true;
    }

    // Deletes the folder of each vanished remote library that no library, of the plugin or of the admin, points into.
    private void DeleteVanishedFolders(Guid serverId, IReadOnlySet<string> gone, string root)
    {
        if (ReadFolders() is not { } folders || folders.Any(folder => string.IsNullOrEmpty(folder.ItemId)))
        {
            return;
        }

        foreach (var id in gone)
        {
            var single = new HashSet<string>([id], StringComparer.OrdinalIgnoreCase);
            if (folders.Any(folder => folder.Locations.Any(location => IsVanishedFolder(location, root, serverId, single))))
            {
                continue;
            }

            try
            {
                var path = FederatedPaths.Resolve(root, FederatedPaths.LibraryFolder(serverId, id));
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    _logger.LogInformation("Deleted the folder of a vanished remote library of a server");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning("Cannot delete the folder of a vanished remote library: {Error}{Detail:l}", exception.GetType().Name, ErrorText.Of(exception));
            }
        }
    }
}
