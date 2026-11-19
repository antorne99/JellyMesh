using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

// The shared libraries, one per type with the folders of every server, kept like the libraries of a server (§10 row 442).
public sealed partial class ServerLibraryManager
{
    private static readonly Guid _sharedOwner = SharedLibraries.Owner;

    /// <inheritdoc />
    public SharedLibrariesState GetSharedState()
    {
        var state = _store.Read(config => new SharedLibrariesState(
            config.SharedLibraries.Enabled,
            false,
            config.SharedLibraries.Libraries.Select(record => new AutomaticLibraryStatus(record.Kind, record.Name) { Adopted = record.Adopted }).ToList(),
            config.SharedLibraries.ReleasedLibraries.ToList(),
            AutomaticLibrariesError.None));
        return state with
        {
            Libraries = WithDisabled(state.Libraries),
            Busy = HasJob(_sharedOwner),
            Error = _errors.GetValueOrDefault(_sharedOwner),
            Conflicts = _conflicts.GetValueOrDefault(_sharedOwner) ?? []
        };
    }

    /// <inheritdoc />
    public AutomaticLibrariesRequest EnableShared() => _store.Read(config => config.FederatedFolderPath.Length == 0)
        ? AutomaticLibrariesRequest.Refused(AutomaticLibrariesOutcome.NoFederatedFolder)
        : Start(_sharedOwner, (_, token) => EnableSharedAsync(token));

    /// <inheritdoc />
    public AutomaticLibrariesRequest DisableShared(bool confirmed)
    {
        // An adopted library is released, not deleted, so it needs no confirmation.
        var names = _store.Read(config => config.SharedLibraries.Libraries.Where(record => !record.Adopted).Select(record => record.Name).ToList());
        if (HasJob(_sharedOwner))
        {
            return AutomaticLibrariesRequest.Refused(AutomaticLibrariesOutcome.Busy);
        }

        return names.Count > 0 && !confirmed
            ? new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.ConfirmationRequired, Task.CompletedTask, names)
            : Start(_sharedOwner, (_, token) => DisableSharedAsync(token));
    }

    // The registry of the shared libraries, while the option is on or libraries are left. It never adopts a library of a
    // server registry for a renamed one.
    private static Registry? SharedRegistry(PluginConfiguration config)
    {
        var shared = config.SharedLibraries;
        if (!shared.Enabled && shared.Libraries.Count == 0)
        {
            return null;
        }

        return new Registry(
            _sharedOwner,
            shared.Libraries,
            shared.ReleasedLibraries,
            IdSet(shared.PreexistingLibraryIds),
            IdSet(ServerRecords(config).Select(record => record.ItemId)),
            IdSet([]),
            shared.FollowedLibraryIds,
            shared.FollowSkipped,
            skipped => shared.FollowSkipped = skipped,
            true,
            []);
    }

    // The records of the configured and removed servers.
    private static IEnumerable<AutomaticLibraryConfig> ServerRecords(PluginConfiguration config) =>
        config.Servers.SelectMany(server => server.AutomaticLibraryRegistry).Concat(config.RemovedServerLibraries.SelectMany(held => held.Libraries));

    // The records of every registry: those of the servers and the shared libraries.
    private static IEnumerable<AutomaticLibraryConfig> AllRecords(PluginConfiguration config) => ServerRecords(config).Concat(config.SharedLibraries.Libraries);

    // The registries each check follows: those of the servers, then the shared one.
    private static IEnumerable<Registry?> FollowedRegistries(PluginConfiguration config) =>
        FollowedServers(config).Select(serverId => LiveRegistry(config, serverId)).Append(SharedRegistry(config));

    // A server folder of any server for the shared owner, the folder of the server otherwise.
    private static Guid? ServerOfFolder(string location, string root) =>
        FederatedPaths.RelativeTo(root, System.IO.Path.TrimEndingDirectorySeparator(location))?.Split(System.IO.Path.DirectorySeparatorChar) is [var server, ..]
        && Guid.TryParseExact(server, "D", out var id)
        && id != _sharedOwner
            ? id
            : null;

    private async Task EnableSharedAsync(CancellationToken cancellationToken)
    {
        _store.Update(config =>
        {
            config.SharedLibraries.Enabled = true;
            config.SharedLibraries.ReleasedLibraries.Clear();
        });

        var folders = ReadFolders();
        if (folders is null)
        {
            MarkSkipped(config => [SharedRegistry(config)]);
            _errors[_sharedOwner] = AutomaticLibrariesError.ReadFailed;
            return;
        }

        Follow(SharedRegistry, folders);
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length > 0)
        {
            ResolveSharedPending(root, folders);
        }

        RecordSharedPreexisting(folders);
        _materializer.PrepareServerFolders();
        if (await ConvergeSharedAsync(folders, true, true, cancellationToken).ConfigureAwait(false))
        {
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Deletes the shared libraries the plugin made and releases the adopted ones (D36), then turns the option off.
    private async Task DisableSharedAsync(CancellationToken cancellationToken)
    {
        _conflicts.TryRemove(_sharedOwner, out _);
        if (!await DeleteAllAsync(SharedRegistry, _sharedOwner, null, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _store.Update(config =>
        {
            config.SharedLibraries.Enabled = false;
            config.SharedLibraries.PendingLibraries.Clear();
            config.SharedLibraries.CreatedLibraryKeys.Clear();
        });
    }

    // Follows the shared registry and gives it the folders the servers have now, with a scan after a change, as after the
    // removal of a server once its titles are gone (D44).
    private async Task ConvergeSharedOnlyAsync(CancellationToken cancellationToken)
    {
        if (!_store.Read(config => config.SharedLibraries.Enabled) || ReadFolders() is not { } folders)
        {
            return;
        }

        if (folders.Any(folder => string.IsNullOrEmpty(folder.ItemId)))
        {
            MarkSkipped(config => [SharedRegistry(config)]);
            return;
        }

        Follow(SharedRegistry, folders);
        if (await ConvergeSharedAsync(folders, false, false, cancellationToken).ConfigureAwait(false))
        {
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Gives each shared library the folders of every server, drops those of servers and remote libraries whose titles are
    // gone, and creates or adopts a missing one: all missing on a switch-on, else those never made. Tells whether one changed.
    private async Task<bool> ConvergeSharedAsync(List<VirtualFolderInfo> folders, bool prepared, bool recreate, CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0 || !_store.Read(config => config.SharedLibraries.Enabled) || _stubs.UsesOlderLayout() is not { } usesOlder)
        {
            return false;
        }

        var olderLayout = usesOlder || StubLayout.HasOlderStubFiles(root);
        KeepSharedCreatedKeys();
        var changed = ResolveSharedPending(root, folders);
        var (servers, records, created) = _store.Read(config => (
            config.Servers.Select(server => (server.Id, Folders: _kinds.ToDictionary(kind => kind, kind => SharedLibraries.FoldersOf(server, kind, root, olderLayout)))).ToList(),
            config.SharedLibraries.Libraries.Select(Copy).ToList(),
            config.SharedLibraries.CreatedLibraryKeys.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        var configured = servers.Select(server => server.Id).ToHashSet();
        var conflicts = new List<LibraryConflict>();
        var waited = false;
        foreach (var kind in _kinds)
        {
            // The type folders that came to hold titles count too, as during a folder migration.
            var planned = SharedLibraries.Plan(kind, servers.Select(server => server.Folders[kind].Concat(TypeFolderPaths(kind, root, server.Id) ?? [])));
            if (records.FirstOrDefault(record => string.Equals(KeyOf(record), planned.Key, StringComparison.OrdinalIgnoreCase)) is { } record)
            {
                changed |= KeepSharedFolders(record, planned, root, olderLayout, configured, folders);
                continue;
            }

            if (!recreate && created.Contains(planned.Key))
            {
                continue;
            }

            var adoption = LibraryAdoption.ClassifyShared(planned, TypeOf(kind), Unclaimed(folders));
            conflicts.AddRange(adoption.Conflicts);
            if (adoption.Adopt is { } adopted)
            {
                RecordShared(planned.Key, new AutomaticLibraryConfig { Kind = kind, Name = adopted.Name, ItemId = adopted.ItemId ?? string.Empty, Adopted = true });
                _logger.LogInformation("Adopted the library {Library}, which has exactly the paths and type of the shared {Kind} library", LogText.Clean(adopted.Name), kind);
                continue;
            }

            if (!adoption.Create)
            {
                continue;
            }

            if (!waited)
            {
                await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
                waited = true;
                if (!prepared)
                {
                    _materializer.PrepareServerFolders();
                }
            }

            changed |= await CreateSharedAsync(planned, cancellationToken).ConfigureAwait(false);
        }

        _conflicts[_sharedOwner] = conflicts;
        return changed;
    }

    // Adds to a shared library the folders it lacks and removes the server folders no longer planned that hold no titles.
    private bool KeepSharedFolders(AutomaticLibraryConfig record, PlannedLibrary planned, string root, bool olderLayout, IReadOnlySet<Guid> configured, List<VirtualFolderInfo> folders)
    {
        if (FindRecorded(record, folders) is not { } folder || !Holds(folder, record, root, _sharedOwner))
        {
            return false;
        }

        var changed = false;
        foreach (var path in planned.Paths.Where(path => !folder.Locations.Any(location => FederatedFolderLinkChecker.IsSamePath(location, path))))
        {
            var outcome = _paths.Add(root, folder.Name, path);
            changed |= outcome == LibraryPathOutcome.Done;
            if (outcome is not (LibraryPathOutcome.Done or LibraryPathOutcome.Unchanged))
            {
                _logger.LogWarning("Cannot add a folder of a server to the shared library {Library}: {Outcome}", LogText.Clean(folder.Name), outcome);
            }
        }

        var stale = folder.Locations.Where(location => IsServerFolder(location, record.Kind, string.Empty, root, _sharedOwner)
            && !planned.Paths.Any(path => FederatedFolderLinkChecker.IsSamePath(location, path))
            && !HoldsTitles(location, record.Kind, root, olderLayout, configured));
        foreach (var location in stale.ToList())
        {
            var outcome = _paths.Remove(root, folder.Name, location);
            changed |= outcome == LibraryPathOutcome.Done;
            if (outcome is not (LibraryPathOutcome.Done or LibraryPathOutcome.Unchanged or LibraryPathOutcome.NoLibrary))
            {
                _logger.LogWarning("Cannot remove a folder of a server from the shared library {Library}: {Outcome}", LogText.Clean(folder.Name), outcome);
            }
        }

        return changed;
    }

    // Whether a server folder may still hold titles: those of a removed server until its titles are gone (D44), the 0.5
    // folders while the titles are in that layout, and a type folder while the registry has titles in it or cannot tell.
    private bool HoldsTitles(string location, ServerLibraryKind kind, string root, bool olderLayout, IReadOnlySet<Guid> configured)
    {
        if (ServerOfFolder(location, root) is not { } serverId)
        {
            return true;
        }

        if (!configured.Contains(serverId))
        {
            return _stubs.HasTitlesOf(serverId);
        }

        if (FederatedFolderLinkChecker.IsSamePath(location, PathOf(kind, root, serverId)))
        {
            return olderLayout;
        }

        return TypeFolderPaths(kind, root, serverId) is not { } held || held.Any(path => FederatedFolderLinkChecker.IsSamePath(path, location));
    }

    private async Task<bool> CreateSharedAsync(PlannedLibrary planned, CancellationToken cancellationToken)
    {
        var folders = ReadFolders();
        var (root, addsToCollections) = _store.Read(config => (config.FederatedFolderPath, config.FederateCollections));
        if (folders is null || root.Length == 0)
        {
            _errors[_sharedOwner] = AutomaticLibrariesError.ReadFailed;
            return false;
        }

        var taken = folders.Select(folder => folder.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = SharedLibraries.NamesFor(planned.Kind, _systemId())
            .Select(candidate => _fileSystem.GetValidFilename(candidate).Trim())
            .FirstOrDefault(stored => stored.Length > 0 && !taken.Contains(stored));
        if (name is null)
        {
            _logger.LogWarning("Cannot create the shared {Kind} library: its names are taken", planned.Kind);
            _errors[_sharedOwner] = AutomaticLibrariesError.NameTaken;
            return false;
        }

        // Who blocks a library holding a folder of this one blocks it before it exists, by the id of its name (D42).
        if (!await BlockSharedBeforeCreateAsync(name, planned, folders, cancellationToken).ConfigureAwait(false))
        {
            _errors[_sharedOwner] = AutomaticLibrariesError.Failed;
            return false;
        }

        // The intent is saved first, so a crash before the record is saved leaves the library recorded at the next check.
        var intent = new PendingLibraryConfig { Kind = planned.Kind, Name = name };
        foreach (var path in planned.Paths)
        {
            intent.Paths.Add(path);
        }

        _store.Update(config =>
        {
            config.SharedLibraries.ReleasedLibraries.Remove(name);
            config.SharedLibraries.PendingLibraries.Add(intent);
        });

        try
        {
            await _libraries.AddVirtualFolder(name, TypeOf(planned.Kind), OptionsFor(planned.Paths, addsToCollections), false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot create the shared {Kind} library: {Error}", planned.Kind, exception.GetType().Name);
            _errors[_sharedOwner] = AutomaticLibrariesError.CreateFailed;
            return ReadFolders() is { } after && ResolveShared(intent, root, after);
        }

        // The server may add a number to the name, so the new library is found by its paths among those not there before.
        var made = ReadFolders()?.FirstOrDefault(folder => !taken.Contains(folder.Name) && Passes(folder, planned.Kind, string.Empty, root, _sharedOwner));
        RecordShared(intent.KeyOf(), new AutomaticLibraryConfig { Kind = planned.Kind, Name = made?.Name ?? name, ItemId = made?.ItemId ?? string.Empty });
        _logger.LogInformation("Created the shared library {Library}", LogText.Clean(made?.Name ?? name));
        return true;
    }

    // Blocks a shared library about to be made for who blocks a library holding one of its folders. False when the blocks
    // could not be written, so it is not made.
    private async Task<bool> BlockSharedBeforeCreateAsync(string name, PlannedLibrary planned, IReadOnlyList<VirtualFolderInfo> folders, CancellationToken cancellationToken)
    {
        var sources = folders
            .Where(folder => Guid.TryParse(folder.ItemId, out _) && (folder.Locations ?? []).Any(location => planned.Paths.Any(path => FederatedFolderLinkChecker.IsSamePath(location, path))))
            .ToList();
        var carries = CarriesFor([new NewLibrary(name, null, planned.Paths, false)], sources);
        if (carries.Count == 0)
        {
            return true;
        }

        try
        {
            await _renamer.BlockAsync(carries, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot block a shared library for the users who blocked a library of its folders, so it is not made: {Error}", exception.GetType().Name);
            return false;
        }
    }

    // Saves a shared library and drops its intent.
    private void RecordShared(string key, AutomaticLibraryConfig record) => _store.Update(config =>
    {
        // An adopted library is no longer one a switch-on found, for any registry (§10 row 441).
        if (record.Adopted)
        {
            ForgetPreexisting(config, record.ItemId);
        }

        var shared = config.SharedLibraries;
        shared.Libraries.Add(record);
        if (!shared.CreatedLibraryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            shared.CreatedLibraryKeys.Add(key);
        }

        RemovePending(shared.PendingLibraries, key);
    });

    // A shared library in the registry counts as made, so that one the admin deletes is not made again.
    private void KeepSharedCreatedKeys() => _store.Update(config =>
    {
        var shared = config.SharedLibraries;
        foreach (var key in shared.Libraries.Select(KeyOf).Where(key => !shared.CreatedLibraryKeys.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            shared.CreatedLibraryKeys.Add(key);
        }
    });

    // Records each shared library asked for before a crash and drops the intents of those never made.
    private bool ResolveSharedPending(string root, List<VirtualFolderInfo> folders)
    {
        var changed = false;
        foreach (var intent in _store.Read(config => config.SharedLibraries.PendingLibraries.ToList()))
        {
            changed |= ResolveShared(intent, root, folders);
        }

        return changed;
    }

    private bool ResolveShared(PendingLibraryConfig intent, string root, List<VirtualFolderInfo> folders)
    {
        if (FindMade(intent, root, _sharedOwner, folders) is not { } made)
        {
            _store.Update(config => RemovePending(config.SharedLibraries.PendingLibraries, intent.KeyOf()));
            return false;
        }

        RecordShared(intent.KeyOf(), new AutomaticLibraryConfig { Kind = intent.Kind, Name = made.Name, ItemId = made.ItemId ?? string.Empty });
        _logger.LogInformation("Recorded the shared library {Library} made before a restart", LogText.Clean(made.Name));
        return true;
    }

    // Called after the follow: the libraries there at switch-on, other than those of a registry, are never deleted.
    private void RecordSharedPreexisting(IReadOnlyList<VirtualFolderInfo> folders) => _store.Update(config =>
    {
        if (SharedRegistry(config) is not { } registry)
        {
            return;
        }

        var visible = registry.Visible(folders);
        var owned = registry.Libraries.Select(record => FindRecorded(record, visible))
            .Concat(ServerRecords(config).Select(record => FindRecorded(record, folders)))
            .OfType<VirtualFolderInfo>()
            .ToList();
        var preexisting = config.SharedLibraries.PreexistingLibraryIds;
        preexisting.Clear();
        foreach (var folder in folders.Where(folder => !owned.Contains(folder) && !string.IsNullOrEmpty(folder.ItemId)))
        {
            preexisting.Add(folder.ItemId!);
        }
    });
}
