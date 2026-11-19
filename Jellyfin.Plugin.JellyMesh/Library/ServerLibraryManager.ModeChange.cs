using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The change of library mode of a server (§10 row 438).
/// </summary>
public sealed partial class ServerLibraryManager
{
    /// <inheritdoc />
    public AutomaticLibrariesRequest ApplyMode(Guid serverId)
    {
        var owner = _store.Read(config => Find(config, serverId));
        return owner is null
            ? AutomaticLibrariesRequest.Refused(AutomaticLibrariesOutcome.UnknownServer)
            : Start(serverId, (id, token) => ApplyModeAsync(id, owner, token));
    }

    /// <inheritdoc />
    public LibraryModeChange? PreviewModeChange(Guid serverId, LibraryMode mode)
    {
        var usesOlder = _stubs.UsesOlderLayout();
        var folders = ReadFolders() ?? [];
        var unclaimed = Unclaimed(folders);
        var (change, carries) = _store.Read<(LibraryModeChange? Change, List<AccessCarry> Carries)>(config =>
        {
            if (Find(config, serverId) is not { } server || server.LibraryMode == mode)
            {
                return (null, []);
            }

            // As the change does: nothing goes while the layout is unknown, and a by-type library takes its type folders.
            var root = config.FederatedFolderPath;
            var plan = root.Length == 0 || usesOlder is not { } older ? [] : LibraryPlan.For(server, root, older || StubLayout.HasOlderStubFiles(root), mode);
            if (mode != LibraryMode.None && plan.Count == 0)
            {
                return (new LibraryModeChange(server.LibraryMode, mode, [], []), []);
            }

            var matched = plan.Select(planned => WithTypeFolders(planned, root, serverId))
                .Select(planned => (Planned: planned, Kept: TakeOverMatch(server, mode, planned, folders)))
                .ToList();
            var kept = matched.Select(match => match.Kept).OfType<AutomaticLibraryConfig>().ToList();
            var retiring = server.AutomaticLibraryRegistry.Where(record => Retires(mode, record) && !kept.Contains(record)).ToList();
            var made = new List<NewLibrary>();
            foreach (var planned in matched.Where(match => match.Kept is null).Select(match => match.Planned))
            {
                // As the change does: a library of the admin with exactly the paths and type is adopted, one in the way stops it.
                var adoption = LibraryAdoption.Classify(planned, TypeOf(planned.Kind), unclaimed, root, serverId);
                if (adoption.Adopt is { ItemId: { } adoptedId } adopted && Guid.TryParse(adoptedId, out var id))
                {
                    made.Add(new NewLibrary(adopted.Name, id, planned.Paths, true));
                }
                else if (adoption.Create)
                {
                    made.Add(new NewLibrary(ChooseName(server.Name, serverId, planned.Suffix, folders) ?? planned.Suffix, null, planned.Paths, false));
                }
            }

            return (new LibraryModeChange(
                server.LibraryMode,
                mode,
                [.. retiring.Where(record => !record.Adopted).Select(record => record.Name)],
                [.. retiring.Where(record => record.Adopted).Select(record => record.Name)]),
                CarriesFor(made, AccessSources(server, mode, folders, root)));
        });
        return change is null || carries.Count == 0 ? change : change with { LostAccess = _renamer.LosingAccess(carries) };
    }

    // Whether a library of the registry belongs to another mode than the one given: one of a type in "per remote library",
    // one of a remote library in "per type", any in "none".
    private static bool Retires(LibraryMode mode, AutomaticLibraryConfig record) => mode switch
    {
        LibraryMode.ByType => record.RemoteLibraryId.Length > 0,
        LibraryMode.ByRemoteLibrary => record.RemoteLibraryId.Length == 0,
        _ => true
    };

    // The old libraries go only once every library the mode wants is in the registry; in "none" at once. With nothing
    // planned, as while the layout is unknown, they stay.
    private static bool ReadyToRetire(RemoteServerConfig server, string root, bool? olderLayout)
    {
        if (server.LibraryMode == LibraryMode.None)
        {
            return true;
        }

        if (olderLayout is not { } older || LibraryPlan.For(server, root, older) is not { Count: > 0 } plan)
        {
            return false;
        }

        return plan.All(planned => server.AutomaticLibraryRegistry.Any(record =>
            !Retires(server.LibraryMode, record) && string.Equals(KeyOf(record), planned.Key, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task ApplyModeAsync(Guid serverId, RemoteServerConfig owner, CancellationToken cancellationToken)
    {
        if (_store.Read(config => Owned(config, owner)?.LibraryMode) is not { } mode)
        {
            return;
        }

        if (_store.Read(config => Owned(config, owner)?.LostAccess.Count > 0))
        {
            _store.Update(config => Owned(config, owner)?.LostAccess.Clear());
        }

        var scanned = false;
        if (mode != LibraryMode.None)
        {
            scanned = await EnableAsync(serverId, owner, mode, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ForgetPendingInNone();
        }

        await RetireAsync(serverId, scanned, cancellationToken).ConfigureAwait(false);
    }

    // In "none" a library asked before a crash is no longer the plugin's to find, as on a switch off, so a library the admin
    // makes later with its name and paths is never taken for one the plugin made.
    private void ForgetPendingInNone()
    {
        if (!_store.Read(config => config.Servers.Any(server => server.LibraryMode == LibraryMode.None && server.PendingLibraries.Count > 0)))
        {
            return;
        }

        _store.Update(config =>
        {
            foreach (var server in config.Servers.Where(server => server.LibraryMode == LibraryMode.None))
            {
                server.PendingLibraries.Clear();
            }
        });
    }

    // A planned library with the type folders that hold titles of its kind, as a by-type library takes them.
    private PlannedLibrary WithTypeFolders(PlannedLibrary wanted, string root, Guid serverId)
        => wanted.RemoteLibraryId.Length > 0 || TypeFolderPaths(wanted.Kind, root, serverId) is not { } held
            ? wanted
            : wanted with { Paths = [.. wanted.Paths, .. held.Where(path => !wanted.Paths.Any(other => FederatedFolderLinkChecker.IsSamePath(other, path)))] };

    // Deletes the plugin libraries of the old mode and releases its adopted ones, once those of the new mode are there and
    // scanned. Called again by each check, so a change stopped halfway goes on.
    private async Task RetireAsync(Guid serverId, bool scanned, CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0)
        {
            return;
        }

        bool? olderLayout = _stubs.UsesOlderLayout() is { } usesOlder ? usesOlder || StubLayout.HasOlderStubFiles(root) : null;
        var (retiring, ready, none) = _store.Read(config => Find(config, serverId) is { } server
            ? (server.AutomaticLibraryRegistry.Any(record => Retires(server.LibraryMode, record)), ReadyToRetire(server, root, olderLayout), server.LibraryMode == LibraryMode.None)
            : (false, false, false));
        if (!retiring)
        {
            return;
        }

        if (!none && !await BlockNewAsync([serverId], cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!ready)
        {
            _logger.LogInformation("The libraries of the previous library mode stay until those of the new mode are made");
            return;
        }

        if (!scanned && !none)
        {
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!none && !await CarryAccessAsync(serverId, root, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var before = _store.Read(config => Find(config, serverId)?.AutomaticLibraryRegistry.Count ?? 0);
        await DeleteAllAsync(
            config => LiveRegistry(config, serverId),
            serverId,
            (config, record) => Find(config, serverId) is { } server && Retires(server.LibraryMode, record) && ReadyToRetire(server, root, olderLayout),
            cancellationToken).ConfigureAwait(false);
        ForgetPendingInNone();

        // Removing a library drops for a while the titles it shared with the new ones; a scan brings them back (F23).
        if (!none && _store.Read(config => Find(config, serverId)?.AutomaticLibraryRegistry.Count ?? 0) < before)
        {
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Before a scan shows the new plugin libraries of a mode change, who blocks an old library holding their folders blocks
    // them too, which only narrows access. False when the blocks could not be written, so nothing is scanned.
    private async Task<bool> BlockNewAsync(IReadOnlyList<Guid> serverIds, CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0 || !_store.Read(config => config.Servers.Any(server => serverIds.Contains(server.Id) && server.AutomaticLibraryRegistry.Any(record => Retires(server.LibraryMode, record)))))
        {
            return true;
        }

        if (ReadFolders() is not { } folders)
        {
            return false;
        }

        var carries = _store.Read(config => config.Servers.Where(server => serverIds.Contains(server.Id)).SelectMany(server => BlockCarries(server, root, folders)).ToList());
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
            _logger.LogError("Cannot block the libraries of the new library mode for the users who blocked the old ones: {Error}", exception.GetType().Name);
            return false;
        }
    }

    // Blocks a library about to be made, by the id of its name, for who blocks an old library holding one of its folders.
    // False when the blocks could not be written.
    private async Task<bool> BlockBeforeCreateAsync(RemoteServerConfig owner, string name, PlannedLibrary planned, string root, IReadOnlyList<VirtualFolderInfo> folders, CancellationToken cancellationToken)
    {
        var carries = _store.Read(config => Owned(config, owner) is { } server && server.AutomaticLibraryRegistry.Any(record => Retires(server.LibraryMode, record))
            ? CarriesFor([new NewLibrary(name, null, planned.Paths, false)], AccessSources(server, server.LibraryMode, folders, root))
            : []);
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
            _logger.LogError("Cannot block a library of the new library mode for the users who blocked the old ones, so it is not made: {Error}", exception.GetType().Name);
            return false;
        }
    }

    // The plugin libraries of the new mode already there, each with the old libraries that held its folders; one with no
    // id yet goes by its name.
    private static IEnumerable<AccessCarry> BlockCarries(RemoteServerConfig server, string root, IReadOnlyList<VirtualFolderInfo> folders)
    {
        if (!server.AutomaticLibraryRegistry.Any(record => Retires(server.LibraryMode, record)))
        {
            return [];
        }

        var made = server.AutomaticLibraryRegistry
            .Where(record => !record.Adopted && !Retires(server.LibraryMode, record))
            .Select(record => (Record: record, Folder: FindRecorded(record, folders)))
            .Where(pair => pair.Folder is not null && Holds(pair.Folder, pair.Record, root, server.Id))
            .Select(pair => new NewLibrary(pair.Folder!.Name, Guid.TryParse(pair.Folder.ItemId, out var id) ? id : null, pair.Folder.Locations, false));
        return CarriesFor(made, AccessSources(server, server.LibraryMode, folders, root));
    }

    // Gives the users the new plugin libraries as they saw the old ones, and records who loses titles. Tells whether the old
    // libraries can go: not while a new one cannot be read as the plugin's.
    private async Task<bool> CarryAccessAsync(Guid serverId, string root, CancellationToken cancellationToken)
    {
        if (ReadFolders() is not { } folders)
        {
            return false;
        }

        var carries = _store.Read(config => Find(config, serverId) is { } server ? AccessCarries(server, root, folders) : []);
        if (carries is null)
        {
            _logger.LogInformation("The libraries of the previous library mode stay until the new ones can be given to the users");
            return false;
        }

        if (carries.Count == 0)
        {
            return true;
        }

        try
        {
            var lost = await _renamer.CarryAccessAsync(carries, cancellationToken).ConfigureAwait(false);
            if (lost.Count > 0)
            {
                _store.Update(config => AddLosses(Find(config, serverId), lost));
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot give the users access to the libraries of the new library mode: {Error}", exception.GetType().Name);
            return false;
        }
    }

    // The libraries of the new mode, adopted ones included, each with the old libraries that held its folders; null while a
    // plugin one has no id or is no longer the plugin's.
    private static List<AccessCarry>? AccessCarries(RemoteServerConfig server, string root, IReadOnlyList<VirtualFolderInfo> folders)
    {
        var made = new List<NewLibrary>();
        foreach (var record in server.AutomaticLibraryRegistry.Where(record => !Retires(server.LibraryMode, record)))
        {
            if (FindRecorded(record, folders) is not { ItemId: { } itemId } folder || !Guid.TryParse(itemId, out var id) || !Holds(folder, record, root, server.Id))
            {
                // An adopted library no longer the plugin's is nobody's to give; a plugin one waits.
                if (record.Adopted)
                {
                    continue;
                }

                return null;
            }

            made.Add(new NewLibrary(folder.Name, id, folder.Locations, record.Adopted));
        }

        return CarriesFor(made, AccessSources(server, server.LibraryMode, folders, root));
    }

    // The old libraries the users' lists name: those of another mode than the one given, and the adopted ones, while the
    // plugin still holds them.
    private static List<VirtualFolderInfo> AccessSources(RemoteServerConfig server, LibraryMode mode, IReadOnlyList<VirtualFolderInfo> folders, string root) =>
        [.. server.AutomaticLibraryRegistry.Where(record => record.Adopted || Retires(mode, record))
            .Select(record => (Record: record, Folder: FindRecorded(record, folders)))
            .Where(pair => pair.Folder is { ItemId: { } id } && Guid.TryParse(id, out _) && Holds(pair.Folder, pair.Record, root, server.Id))
            .Select(pair => pair.Folder!)];

    // For each new library that shares a folder with an old one, the other libraries that held each of its folders.
    private static List<AccessCarry> CarriesFor(IEnumerable<NewLibrary> made, IReadOnlyList<VirtualFolderInfo> sources) =>
        [.. made.Select(library => new AccessCarry(
                library.Name,
                library.Id,
                [.. library.Paths.Select(path => (IReadOnlyList<Guid>)[.. sources
                    .Where(source => source.Locations.Any(location => FederatedFolderLinkChecker.IsSamePath(location, path)))
                    .Select(source => Guid.Parse(source.ItemId!))
                    .Where(id => id != library.Id)])])
            { Adopted = library.Adopted })
            .Where(carry => carry.Folders.Any(held => held.Count > 0))];

    // Records the users who lose titles, keeping those a change resumed after a restart already recorded.
    private static void AddLosses(RemoteServerConfig? server, IReadOnlyList<AccessLoss> lost)
    {
        foreach (var (library, user) in lost.SelectMany(loss => loss.Users.Select(user => (loss.Library, user))))
        {
            if (server is not null && !server.LostAccess.Any(known => string.Equals(known.Library, library, StringComparison.Ordinal) && string.Equals(known.User, user, StringComparison.Ordinal)))
            {
                server.LostAccess.Add(new LostAccessConfig { Library = library, User = user });
            }
        }
    }

    // The recorded users who lost titles, by library, as the page shows them.
    private static List<AccessLoss> LossesOf(IEnumerable<LostAccessConfig> lost) =>
        [.. lost.GroupBy(entry => entry.Library, StringComparer.Ordinal).Select(group => new AccessLoss(group.Key, [.. group.Select(entry => entry.User)]))];

    // The library of another mode than the one given that a planned library of that mode would take over, if any.
    private AutomaticLibraryConfig? TakeOverMatch(RemoteServerConfig server, LibraryMode mode, PlannedLibrary planned, IReadOnlyList<VirtualFolderInfo> folders)
        => server.AutomaticLibraryRegistry.FirstOrDefault(record => Retires(mode, record)
            && FindRecorded(record, folders) is { } folder
            && folder.CollectionType == TypeOf(planned.Kind)
            && folder.Locations.Length == planned.Paths.Count
            && planned.Paths.All(path => folder.Locations.Any(location => FederatedFolderLinkChecker.IsSamePath(location, path)))
            && (record.Adopted || NamesFor(server.Name, server.Id, planned.Suffix).Contains(folder.Name, StringComparer.Ordinal)));

    // A library of the old mode with exactly the paths and type a new one wants takes its place, keeping its id and its
    // permissions: an adopted one always, one the plugin made when it already has the name the new one would take.
    private bool TakeOver(RemoteServerConfig owner, PlannedLibrary planned, List<VirtualFolderInfo> folders)
    {
        var found = _store.Read(config => Owned(config, owner) is { } server ? TakeOverMatch(server, server.LibraryMode, planned, folders) : null);
        if (found is null)
        {
            return false;
        }

        var taken = false;
        _store.Update(config =>
        {
            if (Owned(config, owner) is not { } server || !server.AutomaticLibraryRegistry.Any(record => ReferenceEquals(record, found)))
            {
                return;
            }

            var oldKey = KeyOf(found);
            server.CreatedLibraryKeys.Remove(oldKey);
            found.Kind = planned.Kind;
            found.RemoteLibraryId = planned.RemoteLibraryId;
            if (!server.CreatedLibraryKeys.Contains(planned.Key, StringComparer.OrdinalIgnoreCase))
            {
                server.CreatedLibraryKeys.Add(planned.Key);
            }

            taken = true;
        });
        if (taken)
        {
            _logger.LogInformation("The library {Library} stays in the new library mode, with the same paths", LogText.Clean(found.Name));
        }

        return taken;
    }

    // A library of the new mode: made by the plugin, or the admin's that it adopts.
    private sealed record NewLibrary(string Name, Guid? Id, IReadOnlyList<string> Paths, bool Adopted);
}
