using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

// The servers linked after the automatic migration (Q58, D21, D23, D24): their folders join the libraries holding a
// folder of the same type of another server (§10 row 443), and the settings set the mode, the field of the banner (§10 row 368).
public sealed partial class LayoutMigration
{
    // Set when the scan after the folders of new servers were added was held back; the next run asks for it again.
    private bool _placementScanOwed;

    /// <inheritdoc />
    public LayoutMigrationMode NewServerMode =>
        _store.Read(config => config.LayoutMigration.Mode) == LayoutMigrationMode.Automatic ? LayoutMigrationMode.Automatic : LayoutMigrationMode.Manual;

    /// <inheritdoc />
    public LayoutMigrationChoice SetMode(LayoutMigrationMode mode)
    {
        if (mode is not (LayoutMigrationMode.Manual or LayoutMigrationMode.Automatic))
        {
            return LayoutMigrationChoice.Refused(LayoutMigrationChoiceOutcome.NotAllowed);
        }

        var (root, current) = _store.Read(config => (config.FederatedFolderPath ?? string.Empty, Copy(config.LayoutMigration)));
        if (current.Mode == LayoutMigrationMode.Automatic)
        {
            if (mode == LayoutMigrationMode.Automatic)
            {
                return new LayoutMigrationChoice(LayoutMigrationChoiceOutcome.Chosen, Task.CompletedTask);
            }

            // An unfinished run goes on from the banner, which has no way back to manual (Q57).
            if (current.Step != AutomaticMigrationStep.Done)
            {
                return LayoutMigrationChoice.Refused(LayoutMigrationChoiceOutcome.NotAllowed);
            }

            // Under the lock Kick takes, so that no job starts between the check and the save and reads Automatic.
            lock (_jobLock)
            {
                if (_job is { IsCompleted: false })
                {
                    return LayoutMigrationChoice.Refused(LayoutMigrationChoiceOutcome.Busy);
                }

                _store.Update(config => config.LayoutMigration.Mode = LayoutMigrationMode.Manual);
            }

            _logger.LogInformation("Migration to the server folders: the admin set the new servers to manual");
            return new LayoutMigrationChoice(LayoutMigrationChoiceOutcome.Chosen, Task.CompletedTask);
        }

        if (GetStatus(root) is not null)
        {
            return Choose(mode);
        }

        if (mode == LayoutMigrationMode.Manual)
        {
            return new LayoutMigrationChoice(LayoutMigrationChoiceOutcome.Chosen, Task.CompletedTask);
        }

        // Nothing to migrate: the servers there now are the admin's, and only those linked later are placed.
        var servers = _servers.GetServers().Select(server => server.Id).Distinct().ToList();
        _store.Update(config =>
        {
            var migration = config.LayoutMigration;
            migration.Mode = LayoutMigrationMode.Automatic;
            migration.Step = AutomaticMigrationStep.Done;
            migration.Error = AutomaticMigrationError.None;
            migration.ErrorLibrary = string.Empty;
            migration.PlacedServers.Clear();
            foreach (var server in servers)
            {
                migration.PlacedServers.Add(server);
            }
        });
        _logger.LogInformation("Migration to the server folders: the admin set the new servers to automatic");
        return new LayoutMigrationChoice(LayoutMigrationChoiceOutcome.Chosen, Task.CompletedTask);
    }

    // Adds the folders of each server not placed yet to every library holding a folder of the same type of another server,
    // but not to the plugin libraries (D24, §10 row 443). A failed addition is retried by the next hook, with no error.
    private async Task PlaceNewServersAsync(string root, CancellationToken cancellationToken)
    {
        var servers = _servers.GetServers().Select(server => server.Id).Distinct().ToList();
        var placed = _store.Read(config => config.LayoutMigration.PlacedServers.ToList());
        var fresh = servers.Where(server => !placed.Contains(server)).ToList();
        if (fresh.Count == 0)
        {
            if (placed.Any(server => !servers.Contains(server)))
            {
                SavePlaced(servers, []);
            }

            return;
        }

        await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
        _materializer.PrepareServerFolders();
        if (_registry.UsesOlderLayout() is not { } usesOlder)
        {
            _logger.LogInformation("Migration to the server folders: the stubs are not read yet, so the new servers wait for the next sync");
            return;
        }

        var olderLayout = usesOlder || StubLayout.HasOlderStubFiles(root);

        // The follow first, so a renamed automatic library is known as one (§10 row 385); busy is checked after the read,
        // so that a job started before it is seen (row 384).
        if (_automaticLibraries.FollowForPlacement() is not { } libraries || _automaticLibraries.IsBusy)
        {
            _logger.LogInformation("Migration to the server folders: the libraries are changing or not ready, so the new servers wait for the next sync");
            return;
        }

        var (folders, automatic) = (libraries.Folders, libraries.Automatic);
        var done = new List<Guid>();
        var added = false;
        foreach (var server in fresh)
        {
            if (FoldersToPlace(root, server, olderLayout) is not { } paths)
            {
                _logger.LogInformation("Migration to the server folders: a new server has no imported library read yet, so it waits for the next sync");
                continue;
            }

            var failed = false;
            foreach (var (path, type) in paths)
            {
                foreach (var folder in folders.Where(folder => !automatic.Contains(folder) && !Holds(folder, path) && HoldsOther(folder, root, server, type, servers)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outcome = _paths.Add(root, folder.Name, path);
                    added |= outcome == LibraryPathOutcome.Done;
                    if (!IsDone(outcome))
                    {
                        _logger.LogWarning("Cannot add the folder of a new server to the library {Library}; the next sync tries again", LogText.Clean(folder.Name));
                        failed = true;
                    }
                }
            }

            if (!failed)
            {
                done.Add(server);
            }
        }

        // The new servers stay to place until the scan runs, which waits while a shared library lacks its blocks (D42).
        if ((added || _placementScanOwed) && !await _automaticLibraries.ScanAsync(cancellationToken).ConfigureAwait(false))
        {
            _placementScanOwed = true;
            SavePlaced(servers, []);
            _logger.LogInformation("Migration to the server folders: the library scan waits for a shared library, so the new servers wait for the next sync");
            return;
        }

        _placementScanOwed = false;
        SavePlaced(servers, done);
    }

    // The folders of a new server with their type: its 0.5 folders while the stubs keep the older layout, otherwise one type
    // folder per imported remote library (§10 row 443). Null until a sync has read an imported library of it.
    private List<(string Path, TypeFolder Type)>? FoldersToPlace(string root, Guid server, bool olderLayout)
    {
        if (olderLayout)
        {
            return [(FederatedFolderLayout.ServerMoviesPath(root, server), TypeFolder.Movies), (FederatedFolderLayout.ServerSeriesPath(root, server), TypeFolder.Series)];
        }

        var typeFolders = _store.Read(config => config.Servers.FirstOrDefault(entry => entry.Id == server) is { } found ? LibraryPlan.TypeFolders(found) : []);
        return typeFolders.Count == 0 ? null : typeFolders
            .Select(folder => (FederatedPaths.Resolve(root, folder), TypeFolder.All.First(type => string.Equals(type.Name, Path.GetFileName(folder), StringComparison.Ordinal))))
            .ToList();
    }

    // Whether a library holds a folder of the given type of another configured server, in either layout.
    private static bool HoldsOther(VirtualFolderInfo folder, string root, Guid server, TypeFolder type, IReadOnlyCollection<Guid> servers) =>
        (folder.Locations ?? []).Any(location => LayoutFolderOf(root, location) is { } found
            && found.Server != server
            && servers.Contains(found.Server)
            && found.Type == type);

    // The server and type of a 0.5 server folder or of a type folder of a remote library; null for any other path.
    private static (Guid Server, TypeFolder Type)? LayoutFolderOf(string root, string location)
    {
        var parts = FederatedPaths.RelativeTo(root, Path.TrimEndingDirectorySeparator(location))?.Split(Path.DirectorySeparatorChar);
        var (serverPart, typePart, typeFolders) = parts switch
        {
            [var server, var type] => (server, type, TypeFolder.Written),
            [var server, var library, var type] when FederatedPaths.IsLibraryId(library) => (server, type, TypeFolder.All),
            _ => (null, null, (IReadOnlyList<TypeFolder>)[])
        };
        return Guid.TryParseExact(serverPart, "D", out var id)
            && string.Equals(serverPart, FederatedPaths.ServerFolder(id), StringComparison.Ordinal)
            && typeFolders.FirstOrDefault(folder => string.Equals(folder.Name, typePart, StringComparison.Ordinal)) is { } found
            ? (id, found)
            : null;
    }

    // Keeps the placed servers still configured, with those just placed.
    private void SavePlaced(IReadOnlyCollection<Guid> servers, IReadOnlyCollection<Guid> done) => _store.Update(config =>
    {
        var placed = config.LayoutMigration.PlacedServers;
        var kept = placed.Where(servers.Contains).Concat(done).Distinct().ToList();
        placed.Clear();
        foreach (var server in kept)
        {
            placed.Add(server);
        }
    });

    // Records the servers whose folders the migration added, so only those linked later are placed.
    private void SeedPlaced(IReadOnlyCollection<Guid> servers) => _store.Update(config =>
    {
        foreach (var server in servers.Where(server => !config.LayoutMigration.PlacedServers.Contains(server)))
        {
            config.LayoutMigration.PlacedServers.Add(server);
        }
    });
}
