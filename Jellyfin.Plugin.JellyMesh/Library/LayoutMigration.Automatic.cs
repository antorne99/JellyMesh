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

// The automatic migration (Q55-Q57): it adds the server folders to the libraries on an old path, scans, carries, removes
// the old paths and scans again. Each step is saved, so a restart or a retry resumes there.
public sealed partial class LayoutMigration
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _jobLock = new();
    private Task? _job;
    private bool _disposed;

    /// <summary>
    /// Gets the running automatic migration, or a completed task.
    /// </summary>
    internal Task Running
    {
        get
        {
            lock (_jobLock)
            {
                return _job ?? Task.CompletedTask;
            }
        }
    }

    private bool IsRunning
    {
        get
        {
            lock (_jobLock)
            {
                return _job is { IsCompleted: false };
            }
        }
    }

    /// <inheritdoc />
    public LayoutMigrationChoice Choose(LayoutMigrationMode mode)
    {
        var (root, current) = _store.Read(config => (config.FederatedFolderPath ?? string.Empty, Copy(config.LayoutMigration)));
        if (mode is not (LayoutMigrationMode.Manual or LayoutMigrationMode.Automatic) || (mode == LayoutMigrationMode.Manual && current.Mode == LayoutMigrationMode.Automatic))
        {
            return LayoutMigrationChoice.Refused(LayoutMigrationChoiceOutcome.NotAllowed);
        }

        if (mode == LayoutMigrationMode.Automatic && IsRunning)
        {
            return LayoutMigrationChoice.Refused(LayoutMigrationChoiceOutcome.Busy);
        }

        if (GetStatus(root) is null)
        {
            return LayoutMigrationChoice.Refused(LayoutMigrationChoiceOutcome.NothingToMigrate);
        }

        if (mode == LayoutMigrationMode.Manual)
        {
            _store.Update(config => config.LayoutMigration.Mode = LayoutMigrationMode.Manual);
            return new LayoutMigrationChoice(LayoutMigrationChoiceOutcome.Chosen, Task.Run(RunManualAsync));
        }

        // A retry clears the error and resumes from the step that failed, without redoing the others (Q57).
        _store.Update(config =>
        {
            var migration = config.LayoutMigration;
            if (migration.Mode != LayoutMigrationMode.Automatic || migration.Step == AutomaticMigrationStep.Done)
            {
                migration.Step = AutomaticMigrationStep.AddPaths;
            }

            migration.Mode = LayoutMigrationMode.Automatic;
            migration.Error = AutomaticMigrationError.None;
            migration.ErrorLibrary = string.Empty;
        });
        _logger.LogInformation("Migration to the server folders: the admin chose the automatic migration");
        return new LayoutMigrationChoice(LayoutMigrationChoiceOutcome.Started, Kick());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_jobLock)
        {
            _disposed = true;
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private static LayoutMigrationConfig Copy(LayoutMigrationConfig migration) => new()
    {
        Mode = migration.Mode,
        Step = migration.Step,
        Error = migration.Error,
        ErrorLibrary = migration.ErrorLibrary ?? string.Empty
    };

    private static bool Holds(VirtualFolderInfo folder, string path) =>
        (folder.Locations ?? []).Any(location => FederatedFolderLinkChecker.IsSamePath(location, path));

    private static bool IsDone(LibraryPathOutcome outcome) => outcome is LibraryPathOutcome.Done or LibraryPathOutcome.Unchanged;

    // The hooks after a scan and a sync: the carry at its step, and the job for the others once no error stops it.
    private async Task ContinueAsync(string root, LayoutMigrationConfig migration, CancellationToken cancellationToken)
    {
        if (migration.Error != AutomaticMigrationError.None || migration.Step == AutomaticMigrationStep.Done)
        {
            return;
        }

        // The job waits for scans, so a hook running inside one only starts it and never waits for it.
        if (migration.Step != AutomaticMigrationStep.Carry)
        {
            _ = Kick();
            return;
        }

        if (!IsRunning && await CarryStepAsync(root, cancellationToken).ConfigureAwait(false))
        {
            Save(AutomaticMigrationStep.RemoveOldPaths);
            _ = Kick();
        }
    }

    // Starts the job unless it runs; returns it.
    private Task Kick()
    {
        lock (_jobLock)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (_job is not { IsCompleted: false })
            {
                var token = _shutdown.Token;
                _job = Task.Run(() => RunJobAsync(token));
            }

            return _job;
        }
    }

    private async Task RunManualAsync()
    {
        try
        {
            await RunAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down: the next scan or sync runs it.
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a path.
            _logger.LogError("Migration to the server folders failed: {Error}", exception.GetType().Name);
        }
    }

    private async Task RunJobAsync(CancellationToken cancellationToken)
    {
        var step = AutomaticMigrationStep.AddPaths;
        try
        {
            while (true)
            {
                var (root, migration) = _store.Read(config => (config.FederatedFolderPath ?? string.Empty, Copy(config.LayoutMigration)));
                step = migration.Step;
                if (root.Length == 0 || migration.Mode != LayoutMigrationMode.Automatic || migration.Error != AutomaticMigrationError.None || step == AutomaticMigrationStep.Done)
                {
                    return;
                }

                if (await NextStepAsync(root, step, cancellationToken).ConfigureAwait(false) is not { } next)
                {
                    return;
                }

                Save(next);
                _logger.LogInformation("Migration to the server folders: the automatic migration is at the step {Step}", next);
            }
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down: the step stays saved and the next scan or sync resumes it.
        }
        catch (Exception exception)
        {
            // The type only: a message could hold a path.
            _logger.LogError("The automatic migration to the server folders failed at the step {Step}: {Error}", step, exception.GetType().Name);
            TryStop(AutomaticMigrationError.Failed, null);
        }
    }

    // Runs one step; returns the next, or null when the migration stops or waits there.
    private async Task<AutomaticMigrationStep?> NextStepAsync(string root, AutomaticMigrationStep step, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case AutomaticMigrationStep.AddPaths:
                return await AddPathsAsync(root, cancellationToken).ConfigureAwait(false) ? AutomaticMigrationStep.Scan : null;
            case AutomaticMigrationStep.Scan:
                await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
                return AutomaticMigrationStep.Carry;
            case AutomaticMigrationStep.Carry:
                // Titles that wait stay for the hooks after the next scan or sync.
                return await CarryStepAsync(root, cancellationToken).ConfigureAwait(false) ? AutomaticMigrationStep.RemoveOldPaths : null;
            case AutomaticMigrationStep.RemoveOldPaths:
                await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
                return RemoveOldPaths(root);
            case AutomaticMigrationStep.Finish:
                await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Migration to the server folders: the automatic migration is done");
                return AutomaticMigrationStep.Done;
            default:
                return null;
        }
    }

    // Adds the server folders of each kind to every library holding the old path of that kind (Q56), then checks each
    // server folder of the kinds with old titles is in some library (Q215, Q249). False when it stops or waits.
    private async Task<bool> AddPathsAsync(string root, CancellationToken cancellationToken)
    {
        // With no server there is nothing to add or carry to (Q260).
        var servers = _servers.GetServers().Select(server => server.Id).Distinct().ToList();
        if (servers.Count == 0)
        {
            return false;
        }

        await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
        _materializer.PrepareServerFolders();
        if (ReadFolders() is not { } folders)
        {
            Stop(AutomaticMigrationError.ReadFailed, null);
            return false;
        }

        var kinds = new (string Old, Func<Guid, string> New)[]
        {
            (FederatedFolderLayout.MoviesPath(root), server => FederatedFolderLayout.ServerMoviesPath(root, server)),
            (FederatedFolderLayout.SeriesPath(root), server => FederatedFolderLayout.ServerSeriesPath(root, server))
        };
        foreach (var folder in folders)
        {
            foreach (var path in kinds.Where(kind => Holds(folder, kind.Old)).SelectMany(kind => servers.Select(kind.New)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDone(_paths.Add(root, folder.Name, path)))
                {
                    Stop(AutomaticMigrationError.AddFailed, folder.Name);
                    return false;
                }
            }
        }

        if (LibrariesOf(root, _registry.State) is not { } libraries)
        {
            Stop(AutomaticMigrationError.ReadFailed, null);
            return false;
        }

        // No library holds the old path of a kind with old titles, so its server folders have nowhere to go.
        if (!libraries.ServerFoldersLinked)
        {
            Stop(AutomaticMigrationError.FoldersInNoLibrary, null);
            return false;
        }

        return true;
    }

    // The carry and the emptying of the manual migration, with its checks; true once no old title is left.
    private async Task<bool> CarryStepAsync(string root, CancellationToken cancellationToken)
    {
        if (!HasOldTitles(_registry.State))
        {
            return true;
        }

        if (_servers.GetServers().Count == 0 || LibrariesOf(root, _registry.State) is not { } libraries)
        {
            return false;
        }

        // A server folder left the libraries since the step that added it: that step runs again on retry.
        if (!libraries.ServerFoldersLinked || libraries.OnOldPaths.Any(library => library.MissingPaths.Count > 0))
        {
            Stop(AutomaticMigrationError.NewPathsLeft, null, AutomaticMigrationStep.AddPaths);
            return false;
        }

        await MigrateLockedAsync(root, true, cancellationToken).ConfigureAwait(false);
        return !HasOldTitles(_registry.State);
    }

    // Removes the old paths from every library holding them, once no old title is left.
    private AutomaticMigrationStep? RemoveOldPaths(string root)
    {
        if (HasOldTitles(_registry.State))
        {
            return AutomaticMigrationStep.Carry;
        }

        // An unreadable registry loads empty, so stubs still in the old folders would lose their state (§10 row 371).
        if (OldFoldersHoldStubs(root))
        {
            Stop(AutomaticMigrationError.OldStubsLeft, null);
            return null;
        }

        if (LibrariesOf(root, _registry.State) is not { } libraries)
        {
            Stop(AutomaticMigrationError.ReadFailed, null);
            return null;
        }

        foreach (var library in libraries.OnOldPaths)
        {
            if (!library.OldPaths.All(path => IsDone(_paths.Remove(root, library.Name, path))))
            {
                Stop(AutomaticMigrationError.RemoveFailed, library.Name);
                return null;
            }
        }

        return AutomaticMigrationStep.Finish;
    }

    // Whether a .strm is left under the flat Film or Serie; true as well when they cannot be read.
    private bool OldFoldersHoldStubs(string root)
    {
        try
        {
            return new[] { FederatedFolderLayout.MoviesPath(root), FederatedFolderLayout.SeriesPath(root) }
                .Where(Directory.Exists)
                .Any(folder => Directory.EnumerateFiles(folder, "*.strm", SearchOption.AllDirectories).Any());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot read the old folders before removing them from the libraries: {Error}", exception.GetType().Name);
            return true;
        }
    }

    private List<VirtualFolderInfo>? ReadFolders()
    {
        try
        {
            return _library.GetVirtualFolders();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot read the libraries for the migration to the server folders: {Error}", exception.GetType().Name);
            return null;
        }
    }

    private void Save(AutomaticMigrationStep step) => _store.Update(config => config.LayoutMigration.Step = step);

    // Stops at the step, which the banner shows with the steps left to do by hand (Q57).
    private void Stop(AutomaticMigrationError error, string? library, AutomaticMigrationStep? step = null)
    {
        _logger.LogWarning("The automatic migration to the server folders stopped: {Error}", error);
        _store.Update(config =>
        {
            config.LayoutMigration.Error = error;
            config.LayoutMigration.ErrorLibrary = library ?? string.Empty;
            if (step is { } at)
            {
                config.LayoutMigration.Step = at;
            }
        });
    }

    private void TryStop(AutomaticMigrationError error, string? library)
    {
        try
        {
            Stop(error, library);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError("Cannot save the error of the automatic migration to the server folders: {Error}", exception.GetType().Name);
        }
    }
}
