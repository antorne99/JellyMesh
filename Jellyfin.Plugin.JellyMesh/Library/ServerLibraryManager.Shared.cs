using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

// The shared libraries, one per type with the folders of every server, kept like the libraries of a server (§10 row 442).
public sealed partial class ServerLibraryManager
{
    private static readonly Guid _sharedOwner = SharedLibraries.Owner;

    // A scan held back for a shared library without its blocks is owed once they are written; read under the gate.
    private bool _sharedScanOwed;

    // A validation of the top folders owed after libraries moved or were deleted, run before the next scan; read under the gate.
    private bool _sharedValidationOwed;

    /// <inheritdoc />
    public SharedLibrariesState GetSharedState()
    {
        var state = _store.Read(config => new SharedLibrariesState(
            config.SharedLibraries.Enabled,
            false,
            config.SharedLibraries.Libraries.Select(record => new AutomaticLibraryStatus(record.Kind, record.Name) { Adopted = record.Adopted }).ToList(),
            config.SharedLibraries.ReleasedLibraries.ToList(),
            AutomaticLibrariesError.None)
        {
            Unblocked = config.SharedLibraries.Libraries.Where(record => record.Unblocked).Select(record => record.Name).ToList()
        });
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
    public AutomaticLibrariesRequest DisableShared(SharedLibrariesConfirmation? confirmation)
    {
        if (HasJob(_sharedOwner))
        {
            return AutomaticLibrariesRequest.Refused(AutomaticLibrariesOutcome.Busy);
        }

        // Every library is named, those deleted and those released, after a follow when the gate is free; the job checks
        // them again under the gate (D48).
        FollowSharedIfFree();
        var current = SharedSwitchOff(PendingNumbered(ReadFolders()));
        if (current.ToDelete.Count + current.ToRelease.Count > 0 && (confirmation is null || !SameSwitchOff(confirmation, current)))
        {
            return new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.ConfirmationRequired, Task.CompletedTask, [.. current.ToDelete]) { ToRelease = [.. current.ToRelease] };
        }

        return Start(_sharedOwner, (_, token) => DisableSharedAsync(current, token));
    }

    // The shared libraries a switch-off deletes, with those stored under a number and never recorded, and those it releases.
    private SharedLibrariesConfirmation SharedSwitchOff(IReadOnlyList<VirtualFolderInfo> numbered) => _store.Read(config => new SharedLibrariesConfirmation(
        config.SharedLibraries.Libraries.Where(record => !record.Adopted).Select(record => record.Name).Concat(numbered.Select(folder => folder.Name)).ToList(),
        config.SharedLibraries.Libraries.Where(record => record.Adopted).Select(record => record.Name).ToList()));

    // The libraries the server stored under a number for shared intents before a crash, never recorded: the one with the
    // intent's type and exactly its folders, when none has its name (D55).
    private List<VirtualFolderInfo> PendingNumbered(List<VirtualFolderInfo>? folders)
    {
        var (root, intents) = _store.Read(config => (config.FederatedFolderPath, config.SharedLibraries.PendingLibraries.ToList()));
        if (folders is null || root.Length == 0)
        {
            return [];
        }

        return intents
            .Where(intent => FindMade(intent, root, _sharedOwner, folders) is null)
            .Select(intent => FindAllMade(intent, root, _sharedOwner, folders, name => IsNumbered(name, intent.Name)) is [var only] ? only : null)
            .OfType<VirtualFolderInfo>()
            .Distinct()
            .ToList();
    }

    private static bool SameSwitchOff(SharedLibrariesConfirmation confirmed, SharedLibrariesConfirmation current)
        => confirmed.ToDelete.ToHashSet(StringComparer.Ordinal).SetEquals(current.ToDelete) && confirmed.ToRelease.ToHashSet(StringComparer.Ordinal).SetEquals(current.ToRelease);

    // Follows the shared registry when no job holds the gate, so that a switch-off names the libraries as they are now.
    private void FollowSharedIfFree()
    {
        if (!_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (_store.Read(config => SharedRegistry(config) is not null) && ReadFolders() is { } folders)
            {
                Follow(SharedRegistry, folders);
            }
        }
        finally
        {
            _gate.Release();
        }
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
            await ResolveSharedPendingAsync(root, folders, cancellationToken).ConfigureAwait(false);
        }

        RecordSharedPreexisting(folders);
        _materializer.PrepareServerFolders();
        if (await ConvergeSharedAsync(folders, true, true, cancellationToken).ConfigureAwait(false))
        {
            await QueueScanAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Deletes the shared libraries the plugin made and releases the adopted ones (D36), then turns the option off.
    private async Task DisableSharedAsync(SharedLibrariesConfirmation confirmed, CancellationToken cancellationToken)
    {
        // The follow runs first, so that a library taken for a renamed one is checked against the confirmed names too.
        var folders = ReadFolders();
        if (folders is not null)
        {
            Follow(SharedRegistry, folders);
        }

        // A library made or renamed since the admin confirmed was not named, so the option stays on until a new confirmation.
        var numbered = PendingNumbered(folders);
        if (!SameSwitchOff(confirmed, SharedSwitchOff(numbered)))
        {
            _logger.LogWarning("A shared library changed after the switch-off was confirmed, so the option stays on");
            _errors[_sharedOwner] = AutomaticLibrariesError.Unconfirmed;
            return;
        }

        _conflicts.TryRemove(_sharedOwner, out _);
        var removed = false;
        try
        {
            if (!await DeleteEachAsync(SharedRegistry, _sharedOwner, null, confirmed, () => removed = true, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // Its intent stays until it is gone, so that the next switch-off lists it again (D55).
            foreach (var made in numbered)
            {
                if (!await RemoveSharedAsync(made, cancellationToken).ConfigureAwait(false))
                {
                    _errors[_sharedOwner] = AutomaticLibrariesError.Failed;
                    return;
                }

                removed = true;
            }

            _store.Update(config =>
            {
                config.SharedLibraries.Enabled = false;
                config.SharedLibraries.PendingLibraries.Clear();
                config.SharedLibraries.CreatedLibraryKeys.Clear();
            });
        }
        finally
        {
            // Once the option is off, so that the scan records no library asked for before a crash and not confirmed.
            if (removed)
            {
                await QueueScanAsync(cancellationToken).ConfigureAwait(false);
            }
        }
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
            await QueueScanAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Gives each shared library the folders of every server, drops those of servers and remote libraries whose titles are
    // gone, and creates or adopts a missing one: all missing on a switch-on, else those never made. Tells whether one changed.
    private async Task<bool> ConvergeSharedAsync(List<VirtualFolderInfo> folders, bool prepared, bool recreate, CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0 || !_store.Read(config => config.SharedLibraries.Enabled) || _stubs.UsesOlderLayout() is not { } olderLayout)
        {
            return false;
        }

        KeepSharedCreatedKeys();
        var changed = await BlockUnblockedAsync(folders, cancellationToken).ConfigureAwait(false) | _sharedScanOwed;
        changed |= await ResolveSharedPendingAsync(root, folders, cancellationToken).ConfigureAwait(false);
        var (servers, records, created) = _store.Read(config => (
            config.Servers.Select(server => (server.Id, Folders: ServerLibraryKinds.OfOneType.ToDictionary(kind => kind, kind => SharedLibraries.FoldersOf(server, kind, root, olderLayout)))).ToList(),
            config.SharedLibraries.Libraries.Select(Copy).ToList(),
            config.SharedLibraries.CreatedLibraryKeys.ToHashSet(StringComparer.OrdinalIgnoreCase)));
        var configured = servers.Select(server => server.Id).ToHashSet();
        var conflicts = new List<LibraryConflict>();
        var waited = false;
        foreach (var kind in ServerLibraryKinds.OfOneType)
        {
            // The type folders that came to hold titles count too, as during a folder migration.
            var planned = SharedLibraries.Plan(kind, servers.Select(server => server.Folders[kind].Concat(TypeFolderPaths(kind, root, server.Id) ?? [])));
            if (records.FirstOrDefault(record => string.Equals(KeyOf(record), planned.Key, StringComparison.OrdinalIgnoreCase)) is { } record)
            {
                changed |= KeepSharedFolders(record, planned, root, olderLayout, configured, folders);
                continue;
            }

            // Film and Serie are made even empty; a new type waits until a server has it, then stays.
            if ((!recreate && created.Contains(planned.Key)) || (planned.Paths.Count == 0 && !ServerLibraryKinds.OfOlderLayout.Contains(kind)))
            {
                continue;
            }

            var adoption = LibraryAdoption.ClassifyShared(planned, Unclaimed(folders));
            conflicts.AddRange(adoption.Conflicts);
            if (adoption.Adopt is { } adopted)
            {
                RecordShared(planned.Key, WithPaths(new AutomaticLibraryConfig { Kind = kind, Name = adopted.Name, ItemId = adopted.ItemId ?? string.Empty, Adopted = true }, adopted.Locations));
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
        RememberSharedPaths(changed ? ReadFolders() : folders);

        // A shared library without its blocks, or not told apart yet, is never scanned: the scan waits for a check that
        // blocks or records it (D42).
        if (_store.Read(config => config.SharedLibraries.Libraries.Any(record => record.Unblocked)))
        {
            _errors[_sharedOwner] = AutomaticLibrariesError.Failed;
        }

        return changed && !SharedScanHeld();
    }

    /// <inheritdoc />
    public async Task<bool> ScanAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await QueueScanAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Queues a library scan and waits for it, unless a shared library still lacks its blocks or is not recorded after a
    // new try: the scan is then owed to the next check (D42). Called under the gate.
    private async Task<bool> QueueScanAsync(CancellationToken cancellationToken)
    {
        if (await SharedScanStillHeldAsync(cancellationToken).ConfigureAwait(false))
        {
            _sharedScanOwed = true;
            return false;
        }

        if (_sharedValidationOwed)
        {
            await ValidateTopFoldersAsync(cancellationToken).ConfigureAwait(false);
        }

        await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        _sharedScanOwed = false;
        return true;
    }

    // Writes again the blocks a shared library lacks and records the shared libraries asked for and not recorded; true
    // when one still lacks them or is not recorded, which holds back every scan (D42).
    private async Task<bool> SharedScanStillHeldAsync(CancellationToken cancellationToken)
    {
        if (!SharedScanHeld())
        {
            return false;
        }

        if (ReadFolders() is { } folders)
        {
            await BlockUnblockedAsync(folders, cancellationToken).ConfigureAwait(false);
            var root = _store.Read(config => config.FederatedFolderPath);
            if (root.Length > 0)
            {
                await ResolveSharedPendingAsync(root, folders, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!SharedScanHeld())
        {
            return false;
        }

        _logger.LogWarning("A shared library still lacks the blocks of the users who block a library on its folders, or is not recorded yet, so no library scan runs");
        if (_store.Read(config => config.SharedLibraries.Libraries.Any(record => record.Unblocked)))
        {
            _errors[_sharedOwner] = AutomaticLibrariesError.Failed;
        }

        return true;
    }

    // Whether a shared library lacks its blocks or is not recorded yet, which holds back every scan (D42).
    private bool SharedScanHeld() => _store.Read(config => config.SharedLibraries.HoldsScans());

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

        if (PathOf(kind, root, serverId) is { } serverFolder && FederatedFolderLinkChecker.IsSamePath(location, serverFolder))
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
        var sources = BlockSources(planned, folders);
        if (sources is null || !await BlockSharedAsync(name, null, planned, sources, cancellationToken).ConfigureAwait(false))
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
            await _libraries.AddVirtualFolder(name, TypeOf(planned.Kind), OptionsFor(planned.Kind, planned.Paths, addsToCollections), false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot create the shared {Kind} library: {Error}{Detail:l}", planned.Kind, exception.GetType().Name, _errorLogs.Local.Of(exception));
            _errors[_sharedOwner] = AutomaticLibrariesError.CreateFailed;
            return ReadFolders() is { } failed && await ResolveSharedAsync(intent, root, failed, cancellationToken).ConfigureAwait(false);
        }

        // Unread, the library stays an intent, and no shared check scans until one records it.
        if (ReadFolders() is not { } after)
        {
            _errors[_sharedOwner] = AutomaticLibrariesError.ReadFailed;
            return false;
        }

        // The server may add a number to the name, so the new library is found by exactly its paths among those not there
        // before, by its name first; with more than one, by the name and number the server gives.
        var fresh = after
            .Where(folder => !taken.Contains(folder.Name) && Passes(folder, planned.Kind, string.Empty, root, _sharedOwner) && SameLocations(folder.Locations ?? [], planned.Paths))
            .ToList();
        if ((fresh.FirstOrDefault(folder => string.Equals(folder.Name, name, StringComparison.Ordinal)) ?? (fresh is [var only] ? only : null)) is not { } made)
        {
            return await ResolveSharedAsync(intent, root, after, cancellationToken).ConfigureAwait(false);
        }

        if (!await RecordMadeAsync(intent, made, after, true, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _logger.LogInformation("Created the shared library {Library}", LogText.Clean(made.Name));
        return true;
    }

    // Records a shared library made for an intent; one stored under another name is first blocked by its own id, or else
    // deleted when just made, or kept unblocked when found later (D42, D54). False when it was not recorded as blocked.
    private async Task<bool> RecordMadeAsync(PendingLibraryConfig intent, VirtualFolderInfo made, IReadOnlyList<VirtualFolderInfo> folders, bool justMade, CancellationToken cancellationToken)
    {
        if (!string.Equals(made.Name, intent.Name, StringComparison.Ordinal))
        {
            var planned = new PlannedLibrary(intent.KeyOf(), intent.Kind, string.Empty, string.Empty, [.. intent.Paths]);
            var id = Guid.TryParse(made.ItemId, out var found) ? found : (Guid?)null;
            if (BlockSources(planned, [.. folders.Where(folder => folder != made)]) is not { } sources
                || !await BlockSharedAsync(made.Name, id, planned, sources, cancellationToken).ConfigureAwait(false))
            {
                if (justMade)
                {
                    return await DropUnblockedAsync(intent, made, cancellationToken).ConfigureAwait(false);
                }

                // Found by its name and folders only, it is recorded with no id, so it is never deleted with no confirmation (D54).
                _errors[_sharedOwner] = AutomaticLibrariesError.Failed;
                RecordShared(intent.KeyOf(), WithPaths(new AutomaticLibraryConfig { Kind = intent.Kind, Name = made.Name, Unblocked = true }, made.Locations ?? []));
                return false;
            }
        }

        RecordShared(intent.KeyOf(), WithPaths(new AutomaticLibraryConfig { Kind = intent.Kind, Name = made.Name, ItemId = made.ItemId ?? string.Empty }, made.Locations ?? []));
        return true;
    }

    // The libraries holding a folder of a shared library about to be made, one not scanned yet with the id of its name;
    // null when such an id cannot be told.
    private List<VirtualFolderInfo>? BlockSources(PlannedLibrary planned, IReadOnlyList<VirtualFolderInfo> folders)
    {
        var sources = new List<VirtualFolderInfo>();
        foreach (var folder in folders.Where(folder => (folder.Locations ?? []).Any(location => planned.Paths.Any(path => FederatedFolderLinkChecker.IsSamePath(location, path)))))
        {
            if (Guid.TryParse(folder.ItemId, out _))
            {
                sources.Add(folder);
            }
            else if (_renamer.IdOfName(folder.Name) is { } id)
            {
                sources.Add(new VirtualFolderInfo { Name = folder.Name, CollectionType = folder.CollectionType, Locations = folder.Locations, ItemId = id.ToString("N") });
            }
            else
            {
                _logger.LogWarning("Cannot tell the id of the library {Library} on the folders of the shared {Kind} library, so it is not made", LogText.Clean(folder.Name), planned.Kind);
                return null;
            }
        }

        return sources;
    }

    // Blocks a shared library, by its id or else the id of its name, for who blocks a library holding one of its folders.
    // False when the blocks could not be written.
    private async Task<bool> BlockSharedAsync(string name, Guid? id, PlannedLibrary planned, IReadOnlyList<VirtualFolderInfo> sources, CancellationToken cancellationToken)
    {
        var carries = CarriesFor([new NewLibrary(name, id, planned.Paths, false)], sources);
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
            _logger.LogError("Cannot block a shared library for the users who blocked a library of its folders: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            return false;
        }
    }

    private static AutomaticLibraryConfig WithPaths(AutomaticLibraryConfig record, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            record.Paths.Add(path);
        }

        return record;
    }

    // Keeps the paths each shared library has now, so that only a library with the same paths is taken for it when renamed.
    // A library still without its blocks keeps those it was recorded with, which tell whether it may be deleted (D54).
    private void RememberSharedPaths(IReadOnlyList<VirtualFolderInfo>? folders)
    {
        if (folders is null)
        {
            return;
        }

        bool Differs(AutomaticLibraryConfig record) =>
            !record.Unblocked
            && FindRecorded(record, folders) is { } folder && !(folder.Locations ?? []).SequenceEqual(record.Paths, StringComparer.Ordinal);

        if (!_store.Read(config => config.SharedLibraries.Libraries.Any(Differs)))
        {
            return;
        }

        _store.Update(config =>
        {
            foreach (var record in config.SharedLibraries.Libraries.Where(Differs))
            {
                record.Paths.Clear();
                WithPaths(record, FindRecorded(record, folders)!.Locations ?? []);
            }
        });
    }

    // Deletes a shared library the server stored under another name when its blocks could not be written, before any scan
    // shows it; one that cannot be deleted is recorded as unblocked, so no shared check scans until its blocks are written.
    private async Task<bool> DropUnblockedAsync(PendingLibraryConfig intent, VirtualFolderInfo made, CancellationToken cancellationToken)
    {
        _errors[_sharedOwner] = AutomaticLibrariesError.Failed;
        if (await RemoveSharedAsync(made, cancellationToken).ConfigureAwait(false))
        {
            _store.Update(config => RemovePending(config.SharedLibraries.PendingLibraries, intent.KeyOf()));
            return false;
        }

        RecordShared(intent.KeyOf(), WithPaths(new AutomaticLibraryConfig { Kind = intent.Kind, Name = made.Name, ItemId = made.ItemId ?? string.Empty, Unblocked = true }, made.Locations ?? []));
        return false;
    }

    // Takes a shared library that could not be blocked from the users, then deletes it with no refresh. False when either
    // step failed.
    private async Task<bool> RemoveSharedAsync(VirtualFolderInfo library, CancellationToken cancellationToken)
    {
        var id = Guid.TryParse(library.ItemId, out var found) ? found : (Guid?)null;
        if (!await RemoveFromUsersAsync(library.Name, id, PreferenceKind.EnabledFolders, cancellationToken).ConfigureAwait(false)
            || !await RemoveFromUsersAsync(library.Name, id, PreferenceKind.EnableContentDeletionFromFolders, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await _libraries.RemoveVirtualFolder(library.Name, false).ConfigureAwait(false);
            _sharedValidationOwed = true;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot delete the shared library {Library}, which could not be blocked: {Error}{Detail:l}", LogText.Clean(library.Name), exception.GetType().Name, _errorLogs.Local.Of(exception));
            return false;
        }
    }

    // Writes the blocks a shared library still lacks, by its id or else the id of its name, or else deletes it (D42). Tells
    // whether one got them or went.
    private async Task<bool> BlockUnblockedAsync(List<VirtualFolderInfo> folders, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var record in _store.Read(config => config.SharedLibraries.Libraries.Where(record => record.Unblocked).Select(Copy).ToList()))
        {
            if (FindRecorded(record, folders) is { } folder && !await BlockFoundAsync(record, folder, folders, cancellationToken).ConfigureAwait(false))
            {
                // Only a library found by its recorded id is deleted with no confirmation; another keeps the scans held (D54).
                if (IsOwnShared(record, folder) && await RemoveSharedAsync(folder, cancellationToken).ConfigureAwait(false))
                {
                    ForgetShared(record);
                    folders.Remove(folder);
                    _logger.LogInformation("Deleted the shared library {Library}, which could not be blocked", LogText.Clean(folder.Name));
                    _sharedScanOwed = true;
                    changed = true;
                }

                continue;
            }

            _store.Update(config =>
            {
                foreach (var kept in config.SharedLibraries.Libraries.Where(kept => kept.Kind == record.Kind && string.Equals(kept.Name, record.Name, StringComparison.Ordinal)))
                {
                    kept.Unblocked = false;
                }
            });
            _sharedScanOwed = true;
            changed = true;
        }

        return changed;
    }

    // Writes the blocks of a shared library found for its record, for its recorded and current folders. False when they
    // could not be written.
    private async Task<bool> BlockFoundAsync(AutomaticLibraryConfig record, VirtualFolderInfo folder, IReadOnlyList<VirtualFolderInfo> folders, CancellationToken cancellationToken)
    {
        var paths = record.Paths.Concat((folder.Locations ?? []).Where(location => !record.Paths.Any(path => FederatedFolderLinkChecker.IsSamePath(path, location))));
        var planned = new PlannedLibrary(KeyOf(record), record.Kind, string.Empty, string.Empty, [.. paths]);
        var id = Guid.TryParse(folder.ItemId, out var found) ? found : (Guid?)null;
        return BlockSources(planned, [.. folders.Where(other => other != folder)]) is { } sources
            && await BlockSharedAsync(folder.Name, id, planned, sources, cancellationToken).ConfigureAwait(false);
    }

    // A shared library found by its recorded id, type and exact paths: another may be the admin's, since the id comes from
    // the name (D54).
    private static bool IsOwnShared(AutomaticLibraryConfig record, VirtualFolderInfo folder) =>
        record.ItemId.Length > 0
            && string.Equals(folder.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase)
            && ServerLibraryKinds.HasTypeOf(folder.CollectionType, record.Kind)
            && record.Paths.Count > 0
            && SameLocations(folder.Locations ?? [], record.Paths);

    // Drops a deleted shared library from the registry with its key, so that a later check makes it again.
    private void ForgetShared(AutomaticLibraryConfig record) => _store.Update(config =>
    {
        var shared = config.SharedLibraries;
        foreach (var kept in shared.Libraries.Where(kept => kept.Kind == record.Kind && string.Equals(kept.Name, record.Name, StringComparison.Ordinal)).ToList())
        {
            shared.Libraries.Remove(kept);
        }

        var key = KeyOf(record);
        foreach (var made in shared.CreatedLibraryKeys.Where(made => string.Equals(made, key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            shared.CreatedLibraryKeys.Remove(made);
        }
    });

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
    private async Task<bool> ResolveSharedPendingAsync(string root, List<VirtualFolderInfo> folders, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var intent in _store.Read(config => config.SharedLibraries.PendingLibraries.ToList()))
        {
            changed |= await ResolveSharedAsync(intent, root, folders, cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    // Finds the library of an intent by its name, or else by the name and number the server gives when the name is taken.
    private async Task<bool> ResolveSharedAsync(PendingLibraryConfig intent, string root, List<VirtualFolderInfo> folders, CancellationToken cancellationToken)
    {
        var made = FindMade(intent, root, _sharedOwner, folders)
            ?? (FindAllMade(intent, root, _sharedOwner, folders, name => IsNumbered(name, intent.Name)) is [var only] ? only : null);
        if (made is null)
        {
            _store.Update(config => RemovePending(config.SharedLibraries.PendingLibraries, intent.KeyOf()));
            return false;
        }

        if (!await RecordMadeAsync(intent, made, folders, false, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _logger.LogInformation("Recorded the shared library {Library}", LogText.Clean(made.Name));
        return true;
    }

    // Whether a name is the other followed by a number from 2, with no leading zero, as Jellyfin 12.4 stores a library
    // whose folder name is taken.
    private static bool IsNumbered(string name, string other)
        => name.Length > other.Length
            && name.StartsWith(other, StringComparison.Ordinal)
            && name[other.Length..] is var number
            && number.All(char.IsAsciiDigit)
            && number[0] != '0'
            && number != "1";

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
