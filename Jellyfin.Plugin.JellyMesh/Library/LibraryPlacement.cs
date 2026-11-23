using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Tells which type folders of remote libraries go to the same local libraries: those whose exact set of local libraries is
/// the same hold one stub per title (§10 rows 439 and 507). Its keys mean something only within one reading.
/// </summary>
public sealed class LibraryPlacement
{
    private readonly string _root;
    private readonly Func<Guid, LibraryMode> _modeOf;
    private readonly Dictionary<string, string> _setOfPath;
    private readonly Dictionary<(Guid ServerId, string LibraryId, TypeFolder Type), string> _keys = [];
    private readonly Dictionary<(Guid ServerId, TypeFolder Type), string?> _common;

    private LibraryPlacement(string root, Func<Guid, LibraryMode> modeOf, Dictionary<string, string> setOfPath)
    {
        _root = root;
        _modeOf = modeOf;
        _setOfPath = setOfPath;
        _common = setOfPath
            .Select(entry => (Relative: FederatedPaths.RelativeTo(root, entry.Key), Set: entry.Value))
            .Select(entry => (Folder: entry.Relative is { } relative ? FederatedPaths.TypeFolderAt(relative) : null, entry.Set))
            .Where(entry => entry.Folder is not null)
            .GroupBy(entry => entry.Folder!.Value, entry => entry.Set)
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.Ordinal).Count() == 1 ? group.First() : null);
    }

    /// <summary>
    /// Makes the placement of the libraries read.
    /// </summary>
    /// <param name="folders">The local libraries.</param>
    /// <param name="root">The federated folder.</param>
    /// <param name="modeOf">Gives the library mode of a server, for its folders in no local library.</param>
    /// <returns>The placement.</returns>
    public static LibraryPlacement From(IEnumerable<VirtualFolderInfo> folders, string root, Func<Guid, LibraryMode> modeOf)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(modeOf);

        var setOfPath = folders
            .SelectMany((folder, index) => (folder.Locations ?? [])
                .Where(FederatedFolderLinkChecker.IsPath)
                .Select(location => (Path: FederatedFolderLinkChecker.Normalized(location), Library: index)))
            .GroupBy(entry => entry.Path, entry => entry.Library, FederatedFolderLinkChecker.PathComparer)
            .ToDictionary(group => group.Key, group => string.Join(',', group.Distinct().Order()), FederatedFolderLinkChecker.PathComparer);
        return new LibraryPlacement(root.Length == 0 ? root : FederatedFolderLinkChecker.Normalized(root), modeOf, setOfPath);
    }

    /// <summary>
    /// Reads the local libraries and makes their placement.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="store">The configuration, with the federated folder and the mode of each server.</param>
    /// <returns>The placement.</returns>
    /// <exception cref="System.IO.IOException">The libraries cannot be read for now.</exception>
    /// <exception cref="UnauthorizedAccessException">The configuration of the libraries may not be opened.</exception>
    public static LibraryPlacement Read(ILibraryManager library, IPluginConfigurationStore store)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(store);

        var (root, modes) = store.Read(config => (
            config.FederatedFolderPath,
            config.Servers.DistinctBy(server => server.Id).ToDictionary(server => server.Id, server => server.LibraryMode)));
        return From(library.GetVirtualFolders() ?? [], root, server => modes.GetValueOrDefault(server));
    }

    /// <summary>
    /// Gives the key of the local libraries that hold a type folder, as <see cref="StubPlanInput.PlacementOf"/> needs it. A
    /// folder in none follows the mode of its server: alone, or with the other folders of its server and type.
    /// </summary>
    /// <param name="serverId">The server.</param>
    /// <param name="libraryId">The remote library.</param>
    /// <param name="type">The type folder.</param>
    /// <returns>The key.</returns>
    public string KeyOf(Guid serverId, string libraryId, TypeFolder type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var folder = (serverId, libraryId, type);
        if (!_keys.TryGetValue(folder, out var key))
        {
            _keys[folder] = key = type.Name + SetOf(serverId, libraryId, type);
        }

        return key;
    }

    private string SetOf(Guid serverId, string libraryId, TypeFolder type)
    {
        var path = FederatedFolderLinkChecker.Normalized(FederatedFolderLayout.TypeFolderPath(_root, serverId, libraryId, type));
        if (_setOfPath.TryGetValue(path, out var set))
        {
            return "|in " + set;
        }

        if (_modeOf(serverId) == LibraryMode.ByRemoteLibrary)
        {
            return "|alone " + libraryId;
        }

        return _common.GetValueOrDefault((serverId, type)) is { } common ? "|in " + common : string.Empty;
    }
}
