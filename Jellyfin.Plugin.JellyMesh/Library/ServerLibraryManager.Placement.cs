using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

// The follow before the placement of the servers linked later, and the automatic libraries in that same read (§10 rows 384, 385).
public sealed partial class ServerLibraryManager
{
    /// <inheritdoc />
    public PlacementLibraries? FollowForPlacement()
    {
        // A holder of the lock may be changing the libraries, so placement waits rather than queue behind it.
        if (!_gate.Wait(0))
        {
            return null;
        }

        try
        {
            return _renamer.HasPending ? null : FollowAndFind();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Reads the libraries once, follows the live registries with that read, then finds the automatic libraries in it.
    // A follow that cannot run marks nothing skipped: no check of the sync was missed (decision 173).
    private PlacementLibraries? FollowAndFind()
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

        return _store.Read(config =>
        {
            var automatic = config.Servers.SelectMany(server => server.AutomaticLibraryRegistry)
                .Concat(config.RemovedServerLibraries.SelectMany(held => held.Libraries))
                .Select(record => FindRecorded(record, folders))
                .OfType<VirtualFolderInfo>()
                .ToHashSet();

            // One made and never recorded, as after a crash, passes as the own library of a server with the switch on that lacks it.
            var unrecorded = config.Servers.Any(server => server.AutomaticLibraries && _kinds.Any(kind =>
                server.AutomaticLibraryRegistry.All(record => record.Kind != kind)
                && folders.Any(folder => !automatic.Contains(folder) && Passes(folder, kind, root, server.Id))));
            return unrecorded ? null : new PlacementLibraries(folders, automatic);
        });
    }
}
