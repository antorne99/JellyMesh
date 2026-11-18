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
        var olderLayout = _stubs.UsesOlderLayout() ?? false;
        var folders = ReadFolders() ?? [];
        return _store.Read(config =>
        {
            if (Find(config, serverId) is not { } server || server.LibraryMode == mode)
            {
                return null;
            }

            var root = config.FederatedFolderPath;
            var plan = root.Length == 0 ? [] : LibraryPlan.For(server, root, olderLayout || StubLayout.HasOlderStubFiles(root), mode);
            if (mode != LibraryMode.None && plan.Count == 0)
            {
                return new LibraryModeChange(server.LibraryMode, mode, [], []);
            }

            var kept = plan.Select(planned => TakeOverMatch(server, mode, planned, folders)).OfType<AutomaticLibraryConfig>().ToList();
            var retiring = server.AutomaticLibraryRegistry.Where(record => Retires(mode, record) && !kept.Contains(record)).ToList();
            return new LibraryModeChange(
                server.LibraryMode,
                mode,
                [.. retiring.Where(record => !record.Adopted).Select(record => record.Name)],
                [.. retiring.Where(record => record.Adopted).Select(record => record.Name)]);
        });
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

        var scanned = false;
        if (mode != LibraryMode.None)
        {
            scanned = await EnableAsync(serverId, owner, mode, cancellationToken).ConfigureAwait(false);
        }

        await RetireAsync(serverId, scanned, cancellationToken).ConfigureAwait(false);
    }

    // In "none" with no library left, a library asked before a crash is no longer the plugin's to find, as on a switch off.
    private void ForgetPendingInNone(Guid serverId) => _store.Update(config =>
    {
        if (Find(config, serverId) is { LibraryMode: LibraryMode.None, AutomaticLibraryRegistry.Count: 0, PendingLibraries.Count: > 0 } server)
        {
            server.PendingLibraries.Clear();
        }
    });

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

        if (!ready)
        {
            _logger.LogInformation("The libraries of the previous library mode stay until those of the new mode are made");
            return;
        }

        if (!scanned && !none)
        {
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await DeleteAllAsync(
            config => LiveRegistry(config, serverId),
            serverId,
            (config, record) => Find(config, serverId) is { } server && Retires(server.LibraryMode, record) && ReadyToRetire(server, root, olderLayout),
            cancellationToken).ConfigureAwait(false);
        ForgetPendingInNone(serverId);
    }

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
}
