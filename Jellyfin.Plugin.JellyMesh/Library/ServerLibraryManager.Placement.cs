using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

// The follow before the placement of the servers linked later, and the automatic libraries in that same read (§10 rows 384, 385).
public sealed partial class ServerLibraryManager
{
    // Guards the placement follow state below. Lock order: the gate if held, then this lock, then the store; the gate is
    // never taken under this lock.
    private readonly Lock _followLock = new();
    private long _completeFollows;
    private int _placementFollows;
    private bool _skipDeferred;

    /// <inheritdoc />
    public PlacementLibraries? FollowForPlacement()
    {
        // Counted before the gate, so that a sync finding the gate held leaves its skipped mark to this follow (§10 row 394).
        lock (_followLock)
        {
            _placementFollows++;
        }

        var gated = false;
        var followed = false;
        try
        {
            // A holder of the lock may be changing the libraries, so placement waits rather than queue behind it.
            gated = _gate.Wait(0);
            if (!gated || _renamer.HasPending || FollowLive() is not { } read)
            {
                return null;
            }

            followed = true;
            return FindAutomatic(read.Folders, read.Root);
        }
        finally
        {
            try
            {
                EndPlacementFollow(followed);
            }
            finally
            {
                if (gated)
                {
                    _gate.Release();
                }
            }
        }
    }

    // Reads the libraries once and follows the live registries with that read; null when the follow cannot run.
    // A follow that cannot run marks nothing skipped, unless a sync left its mark to it (decision 173, §10 row 394).
    private (List<VirtualFolderInfo> Folders, string Root)? FollowLive()
    {
        var (serverIds, root) = _store.Read(config => (FollowedServers(config), config.FederatedFolderPath));
        if (root.Length == 0 || ReadFolders() is not { } folders)
        {
            return null;
        }

        // A library with no id may be a renamed one the follow cannot match yet (decision 213).
        if (serverIds.Count > 0 && folders.Any(folder => string.IsNullOrEmpty(folder.ItemId)))
        {
            return null;
        }

        foreach (var serverId in serverIds)
        {
            Follow(serverId, folders);
        }

        return (folders, root);
    }

    // Finds the automatic libraries in the read the follow used, or null while one may lack its record (§10 rows 384, 393, 395).
    private PlacementLibraries? FindAutomatic(List<VirtualFolderInfo> folders, string root) => _store.Read(config =>
    {
        // A held library renamed in the dashboard is not followed, so it is found by its folder too (§10 row 395).
        var automatic = config.Servers.SelectMany(server => server.AutomaticLibraryRegistry)
            .Concat(config.RemovedServerLibraries.SelectMany(held => held.Libraries))
            .Select(record => FindRecorded(record, folders))
            .Concat(folders.Where(folder => config.RemovedServerLibraries.Any(held => PassesAsHeld(folder, held, root))))
            .OfType<VirtualFolderInfo>()
            .ToHashSet();

        // One made and never recorded, as after a crash, passes as the own library of a server with the switch on that lacks
        // it; one there at switch-on or left to the admin is the admin's (§10 row 393).
        var unrecorded = config.Servers.Any(server =>
        {
            var preexisting = IdSet(server.PreexistingLibraryIds);
            return server.AutomaticLibraries && _kinds.Any(kind =>
                server.AutomaticLibraryRegistry.All(record => record.Kind != kind)
                && folders.Any(folder => !automatic.Contains(folder)
                    && !preexisting.Contains(folder.ItemId ?? string.Empty)
                    && !server.ReleasedAutomaticLibraries.Contains(folder.Name, StringComparer.Ordinal)
                    && Passes(folder, kind, root, server.Id)));
        });
        return unrecorded ? null : new PlacementLibraries(folders, automatic);
    });

    // A library on the folder of a removed server, other than one there at switch-on or released to the admin (§10 rows 395, 402).
    private static bool PassesAsHeld(VirtualFolderInfo folder, RemovedServerLibrariesConfig held, string root) =>
        !(folder.ItemId is { Length: > 0 } id && held.PreexistingLibraryIds.Contains(id, StringComparer.OrdinalIgnoreCase))
        && !held.ReleasedLibraries.Contains(folder.Name, StringComparer.Ordinal)
        && _kinds.Any(kind => Passes(folder, kind, root, held.ServerId));

    // A complete follow covers the checks of the syncs that found the gate held meanwhile; otherwise their mark is made now.
    private void EndPlacementFollow(bool followed)
    {
        lock (_followLock)
        {
            _placementFollows--;
            if (followed)
            {
                _completeFollows++;
                _skipDeferred = false;
            }
            else if (_skipDeferred && _placementFollows == 0)
            {
                _skipDeferred = false;
                MarkSkipped(config => FollowedServers(config).Select(serverId => LiveRegistry(config, serverId)));
            }
        }
    }

    private long CompleteFollows()
    {
        lock (_followLock)
        {
            return _completeFollows;
        }
    }

    // The mark of a sync that found the gate held: none when a complete follow ended since the sync started, and left to the
    // placement follow running now (§10 row 394).
    private void MarkSyncSkipped(long completeFollows)
    {
        lock (_followLock)
        {
            if (_completeFollows != completeFollows)
            {
                return;
            }

            if (_placementFollows > 0)
            {
                _skipDeferred = true;
                return;
            }

            MarkSkipped(config => FollowedServers(config).Select(serverId => LiveRegistry(config, serverId)));
        }
    }
}
