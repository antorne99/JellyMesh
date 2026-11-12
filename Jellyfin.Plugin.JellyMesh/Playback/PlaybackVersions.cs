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
    /// registry, or an item Jellyfin links with it, directly or through a shared primary.
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

        // The other files of a local film link up to it and not down from it, so the two climbs must meet.
        var linked = CopiesOf(itemId);
        var copies = CopiesOf(versionId);
        return linked.ContainsKey(itemId) && copies.ContainsKey(versionId) && (linked.ContainsKey(versionId) || copies.Keys.Any(linked.ContainsKey));
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
        var linked = CopiesOf(itemId);
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

        var items = VisibleStubs(itemId, stub, group, linked, user);
        return items.Count == 1 ? itemId : items[StubRanking.Best([.. items.Keys], _registry.Find, CurrentLookup())];
    }

    /// <summary>
    /// Lists the other stubs of the title the user sees, best first by §4.3: where a playback goes when its version cannot
    /// serve (Q44, D22). Each one is checked with the same rule as a named version, classification included.
    /// </summary>
    /// <param name="itemId">The item of the version that starts.</param>
    /// <param name="stub">Its stub.</param>
    /// <param name="user">The user.</param>
    /// <returns>The other versions, empty when the title has none the user sees.</returns>
    public IReadOnlyList<PlaybackCandidate> Fallbacks(Guid itemId, StubEntry stub, User user)
    {
        ArgumentNullException.ThrowIfNull(stub);
        ArgumentNullException.ThrowIfNull(user);

        var group = _registry.FindGroup(stub.Id);
        var linked = CopiesOf(itemId);
        if (group is null && linked.Count < 2)
        {
            return [];
        }

        var items = VisibleStubs(itemId, stub, group, linked, user);
        items.Remove(stub.Id);
        if (items.Count == 0)
        {
            return [];
        }

        return StubRanking.Order([.. items.Keys], _registry.Find, CurrentLookup())
            .Select(stubId => _registry.Find(stubId) is { } entry ? new PlaybackCandidate(items[stubId], entry) : null)
            .OfType<PlaybackCandidate>()
            .ToList();
    }

    // Only the stubs Jellyfin links here can be served under this item: the client reports the version on it.
    private Dictionary<string, Guid> VisibleStubs(Guid itemId, StubEntry stub, VersionGroup? group, Dictionary<Guid, Video> linked, User user)
    {
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

        return items;
    }

    // The items Jellyfin links with an item, without extras: a trailer is owned by its film but is no copy of it.
    private Dictionary<Guid, Video> CopiesOf(Guid itemId)
    {
        return _stubs.LinkedVersionsOf(itemId)
            .Where(entry => entry.Value.ExtraType is null)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
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
