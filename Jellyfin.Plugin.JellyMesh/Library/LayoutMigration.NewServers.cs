using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

// The servers linked after the automatic migration (Q58, D21, D23, D24): their folders join the libraries holding a
// folder of the same kind of another server, and the settings set the mode, the field of the banner (§10 row 368).
public sealed partial class LayoutMigration
{
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

    // Adds the folders of each server not placed yet to every library holding a folder of the same kind of another server,
    // but not to the automatic libraries (D24). A failed addition is retried by the next hook, with no error.
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

        // The follow first, so a renamed automatic library is known as one (§10 row 385); busy is checked after the read,
        // so that a job started before it is seen (row 384).
        if (_automaticLibraries.FollowForPlacement() is not { } libraries || _automaticLibraries.IsBusy)
        {
            _logger.LogInformation("Migration to the server folders: the libraries are changing or not ready, so the new servers wait for the next sync");
            return;
        }

        var (folders, automatic) = (libraries.Folders, libraries.Automatic);
        var kinds = new Func<Guid, string>[]
        {
            server => FederatedFolderLayout.ServerMoviesPath(root, server),
            server => FederatedFolderLayout.ServerSeriesPath(root, server)
        };
        var done = new List<Guid>();
        var added = false;
        foreach (var server in fresh)
        {
            var failed = false;
            foreach (var pathOf in kinds)
            {
                var path = pathOf(server);
                var others = servers.Where(other => other != server).Select(pathOf).ToList();
                foreach (var folder in folders.Where(folder => !automatic.Contains(folder) && !Holds(folder, path) && others.Any(other => Holds(folder, other))))
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

        SavePlaced(servers, done);
        if (added)
        {
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
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
