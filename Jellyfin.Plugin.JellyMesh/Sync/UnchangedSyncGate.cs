using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// <see cref="IUnchangedSyncGate"/> kept in memory: after a restart the first sync runs. The local library is read as the
/// ids of its films, episodes and series and its libraries, which no event is needed for, and as a count of the items
/// Jellyfin added, removed or updated outside the federated folder, or the administrator edited, for changes that keep
/// the id (Q386).
/// </summary>
public sealed class UnchangedSyncGate : IUnchangedSyncGate, IDisposable
{
    private static readonly BaseItemKind[] _localKinds = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series, BaseItemKind.Video];

    private readonly IFederationIndex _index;
    private readonly IRemoteServerRegistry _servers;
    private readonly IPluginConfigurationStore _store;
    private readonly IManualMergeStore _merges;
    private readonly ILibraryManager _library;
    private readonly StubRegistry _registry;
    private readonly StubUrlBuilder _urls;
    private long _libraryEvents;
    private volatile SyncInputs? _baseline;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnchangedSyncGate"/> class. It watches the library from here on.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="servers">The server registry.</param>
    /// <param name="store">The configuration.</param>
    /// <param name="merges">The merges made by the administrator.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="registry">The stub registry.</param>
    /// <param name="urls">What builds the URL of a stub, which the signing key and the server address change.</param>
    public UnchangedSyncGate(
        IFederationIndex index,
        IRemoteServerRegistry servers,
        IPluginConfigurationStore store,
        IManualMergeStore merges,
        ILibraryManager library,
        StubRegistry registry,
        StubUrlBuilder urls)
    {
        _index = index;
        _servers = servers;
        _store = store;
        _merges = merges;
        _library = library;
        _registry = registry;
        _urls = urls;
        _library.ItemAdded += OnItemAddedOrRemoved;
        _library.ItemRemoved += OnItemAddedOrRemoved;
        _library.ItemUpdated += OnItemUpdated;
    }

    /// <inheritdoc />
    public SyncInputs Capture()
    {
        // The counters first: a change made while the rest is read shows at the next capture.
        var events = Interlocked.Read(ref _libraryEvents);
        var generation = _registry.Generation;
        var snapshot = _index.Snapshot;
        var catalogs = snapshot.Servers.OrderBy(catalog => catalog.ServerId).ToList();
        var (root, isProtected, writesCollections) = _store.Read(config => (config.FederatedFolderPath, config.MetadataProtection, config.FederateCollections));

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"index {snapshot.Health}\n");
        foreach (var catalog in catalogs)
        {
            text.Append(CultureInfo.InvariantCulture, $"catalog {catalog.ServerId:N} {catalog.State}\n");
        }

        foreach (var server in _servers.GetServers().OrderBy(server => server.Id))
        {
            text.Append(CultureInfo.InvariantCulture, $"server {server.Id:N} {server.Mode} {server.Name}\n{string.Join('\n', server.ImportedLibraryIds.Order(StringComparer.Ordinal))}\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"settings {isProtected} {writesCollections} {root}\n{_urls.Build("probe")}\n");
        foreach (var folder in _library.GetVirtualFolders().OrderBy(folder => folder.ItemId, StringComparer.Ordinal).ThenBy(folder => folder.Name, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"library {folder.ItemId} {folder.CollectionType} {folder.Name}\n{string.Join('\n', (folder.Locations ?? []).Order(StringComparer.Ordinal))}\n");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(text.ToString()));
        var ids = _library.GetItemIds(new InternalItemsQuery { IncludeItemTypes = _localKinds, Recursive = true, IsVirtualItem = false }).ToArray();
        Array.Sort(ids);
        Span<byte> bytes = stackalloc byte[16];
        foreach (var id in ids)
        {
            id.TryWriteBytes(bytes);
            hash.AppendData(bytes);
        }

        IReadOnlyList<object> references = [.. catalogs.Select(catalog => (object)catalog.Items), _merges.Merges];
        return new SyncInputs(references, Convert.ToHexString(hash.GetHashAndReset()), events, generation);
    }

    /// <inheritdoc />
    public bool IsUnchanged(SyncInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return _baseline is { } baseline && baseline.SameAs(inputs);
    }

    /// <inheritdoc />
    public void Remember(SyncInputs inputs) => _baseline = inputs;

    /// <inheritdoc />
    public void Forget() => _baseline = null;

    /// <inheritdoc />
    public void Dispose()
    {
        _library.ItemAdded -= OnItemAddedOrRemoved;
        _library.ItemRemoved -= OnItemAddedOrRemoved;
        _library.ItemUpdated -= OnItemUpdated;
    }

    private static bool IsTitle(BaseItem? item) => item is Video or Series;

    // A stub too: one deleted and made again keeps its id but loses its links.
    private void OnItemAddedOrRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (IsTitle(e.Item))
        {
            Interlocked.Increment(ref _libraryEvents);
        }
    }

    // A stub only for an edit from the web admin, such as splitting its versions (D5): the plugin and scans save stubs
    // as imports, and counting those would keep every sync after a change from skipping.
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (IsTitle(e.Item)
            && (e.UpdateReason.HasFlag(ItemUpdateType.MetadataEdit)
                || !FederatedPaths.IsInside(_store.Read(config => config.FederatedFolderPath), e.Item.Path)))
        {
            Interlocked.Increment(ref _libraryEvents);
        }
    }
}
