using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Tells which versions of a title a <c>PlaybackInfo</c> may serve, and which one starts when the client names none
/// (Q10, D1). It reads the registry and the links of Jellyfin, never the whole library.
/// </summary>
public sealed class PlaybackVersions
{
    private readonly StubLookup _stubs;
    private readonly IStubRegistry _registry;
    private readonly ILibraryManager _libraryManager;
    private readonly IFederationIndex _index;
    private readonly IPluginConfigurationStore _store;
    private IndexItemLookup? _lookup;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackVersions"/> class.
    /// </summary>
    /// <param name="stubs">The lookup of stubs and links.</param>
    /// <param name="registry">The registry of stubs and groups.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="index">The index, for the quality of the stubs.</param>
    /// <param name="store">The configuration, for the federated folder.</param>
    public PlaybackVersions(StubLookup stubs, IStubRegistry registry, ILibraryManager libraryManager, IFederationIndex index, IPluginConfigurationStore store)
    {
        _stubs = stubs;
        _registry = registry;
        _libraryManager = libraryManager;
        _index = index;
        _store = store;
    }

    /// <summary>
    /// Tells whether an item is a version of the title of another: the same item, a member of the same group of the
    /// registry, or an item Jellyfin links with it.
    /// </summary>
    /// <param name="itemId">The item of the route.</param>
    /// <param name="versionId">The version the client names.</param>
    /// <returns>True for a version of the title.</returns>
    public bool IsVersionOf(Guid itemId, Guid versionId)
    {
        if (itemId.Equals(versionId))
        {
            return true;
        }

        if (GroupOf(itemId) is { } group && GroupOf(versionId) is { } other && string.Equals(group.Id, other.Id, StringComparison.Ordinal))
        {
            return true;
        }

        return _stubs.LinkedVersionsOf(itemId).ContainsKey(versionId);
    }

    /// <summary>
    /// Picks the version that starts under a stub when the client names none: the local copy if the user sees it, otherwise
    /// the best stub the user sees, by §4.3.
    /// </summary>
    /// <param name="itemId">The item of the route, a stub the user sees.</param>
    /// <param name="stub">Its stub.</param>
    /// <param name="user">The user.</param>
    /// <returns>The item of the version.</returns>
    public Guid Choose(Guid itemId, StubEntry stub, User user)
    {
        ArgumentNullException.ThrowIfNull(stub);
        ArgumentNullException.ThrowIfNull(user);

        var group = _registry.FindGroup(stub.Id);
        var linked = _stubs.LinkedVersionsOf(itemId);
        if (group is null && linked.Count < 2)
        {
            return itemId;
        }

        var root = _store.Read(config => config.FederatedFolderPath);
        var locals = (group?.LocalItemIds ?? [])
            .Concat(linked.Values.Where(video => !FederatedPaths.IsInside(root, video.Path) && !PlaybackRoutes.TryGetStubId(video.ShortcutPath, out _)).Select(video => video.Id));
        foreach (var local in locals.Distinct())
        {
            if (_libraryManager.GetItemById<BaseItem>(local, user) is Video)
            {
                return local;
            }
        }

        // Only the stubs Jellyfin links here can be served under this item: the client reports the version on it.
        var items = new Dictionary<string, Guid>(StringComparer.Ordinal) { [stub.Id] = itemId };
        foreach (var video in linked.Values)
        {
            if (PlaybackRoutes.TryGetStubId(video.ShortcutPath, out var stubId)
                && !items.ContainsKey(stubId)
                && (group is null || group.StubIds.Contains(stubId, StringComparer.Ordinal))
                && _registry.Find(stubId) is not null
                && _libraryManager.GetItemById<BaseItem>(video.Id, user) is not null)
            {
                items[stubId] = video.Id;
            }
        }

        return items.Count == 1 ? itemId : items[StubRanking.Best([.. items.Keys], _registry.Find, CurrentLookup())];
    }

    private VersionGroup? GroupOf(Guid itemId)
    {
        return _stubs.Find(itemId) is { } stub ? _registry.FindGroup(stub.Id) : _registry.FindGroupOfLocal(itemId);
    }

    // Builds the lookup again only when the index has a new snapshot.
    private IndexItemLookup CurrentLookup()
    {
        var snapshot = _index.Snapshot;
        var lookup = _lookup;
        if (lookup is null || !ReferenceEquals(lookup.Snapshot, snapshot))
        {
            lookup = IndexItemLookup.Build(snapshot);
            _lookup = lookup;
        }

        return lookup;
    }
}
