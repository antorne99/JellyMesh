using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Dedup;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IMergeWatchStateCarry"/> through the watch state carrier. An entity is an item that names no primary: its
/// favorite, rating and play count are the title's, so a version that was an alternate before the merge gives nothing.
/// </summary>
public sealed class MergeWatchStateCarry : IMergeWatchStateCarry
{
    private readonly StubRegistry _registry;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IWatchStateCarrier _carrier;
    private readonly ILogger<MergeWatchStateCarry> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MergeWatchStateCarry"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="store">The configuration, for the federated folder.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="carrier">The watch state carrier.</param>
    /// <param name="logger">The logger.</param>
    public MergeWatchStateCarry(
        StubRegistry registry,
        IPluginConfigurationStore store,
        ILibraryManager library,
        IWatchStateCarrier carrier,
        ILogger<MergeWatchStateCarry> logger)
    {
        _registry = registry;
        _store = store;
        _library = library;
        _carrier = carrier;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<MergeHead> FindHeads(IReadOnlyList<MergedCopy> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);

        var root = _store.Read(config => config.FederatedFolderPath) ?? string.Empty;
        if (root.Length == 0)
        {
            return [];
        }

        var stubs = _registry.State.Stubs;
        var heads = new List<MergeHead>();
        foreach (var copy in copies)
        {
            if (copy.IsLocal)
            {
                if (Guid.TryParse(copy.ItemId, out var id) && _library.GetItemById(id) is Video { PrimaryVersionId: null } local)
                {
                    heads.Add(new MergeHead(local.Id, null));
                }

                continue;
            }

            if (!Guid.TryParse(copy.ServerId, out var server))
            {
                continue;
            }

            var stub = stubs.FirstOrDefault(entry => entry.ServerId == server
                && entry.Sources.Any(source => string.Equals(source.ItemId, copy.ItemId, StringComparison.Ordinal)));
            if (stub is not null && FindStub(root, stub) is { PrimaryVersionId: null } item)
            {
                heads.Add(new MergeHead(item.Id, stub));
            }
        }

        return heads.DistinctBy(head => head.ItemId).ToList();
    }

    /// <inheritdoc />
    public async Task<WatchStateCarryReport?> CarryAsync(IReadOnlyList<MergeHead> heads, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heads);

        var root = _store.Read(config => config.FederatedFolderPath) ?? string.Empty;
        if (root.Length == 0 || heads.Count < 2)
        {
            return null;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var moves = MovesOf(root, heads);
            if (moves.Count == 0)
            {
                return null;
            }

            var report = await _carrier.CarryAsync(moves, cancellationToken).ConfigureAwait(false);
            if (report.Skipped > 0)
            {
                _logger.LogWarning("The watch state of {Skipped} merged titles was not carried for every user", report.Skipped);
            }

            return report;
        }
        finally
        {
            _registry.Exit();
        }
    }

    private List<WatchStateMove> MovesOf(string root, IReadOnlyList<MergeHead> heads)
    {
        var groups = heads
            .Select(head => head.Stub is { } stub ? _registry.FindGroup(stub.Id) : _registry.FindGroupOfLocal(head.ItemId))
            .OfType<VersionGroup>()
            .DistinctBy(group => group.Id)
            .ToList();
        if (groups.Count != 1)
        {
            _logger.LogWarning("Cannot carry the watch state of a manual merge: its copies are not linked as one title");
            return [];
        }

        var group = groups[0];
        var members = new List<(Video Item, Guid? ServerId)>();
        foreach (var stub in group.StubIds.Select(_registry.Find).OfType<StubEntry>())
        {
            if (FindStub(root, stub) is { } item)
            {
                members.Add((item, stub.ServerId));
            }
        }

        members.AddRange(group.LocalItemIds.Select(id => _library.GetItemById(id)).OfType<Video>().Select(item => (item, (Guid?)null)));
        var primaries = members.Where(member => member.Item.PrimaryVersionId is null).ToList();
        if (primaries.Count != 1)
        {
            _logger.LogWarning("Cannot carry the watch state of a manual merge: its copies are not linked as one title");
            return [];
        }

        var primary = primaries[0].Item;
        var libraries = LibrariesOf(primary);
        var targets = members.Select(member => new WatchStateTarget(member.Item.Id, member.ServerId)).ToList();
        var moves = new List<WatchStateMove>();
        foreach (var head in heads)
        {
            // Only two entities of one library become one; in different libraries they stay apart (Q23).
            if (head.Stub is { } stub && head.ItemId != primary.Id
                && _library.GetItemById(head.ItemId) is { } item && LibrariesOf(item).Overlaps(libraries))
            {
                moves.Add(WatchStateMove.OfMerge(stub, head.ItemId, targets));
            }
        }

        return moves;
    }

    private HashSet<Guid> LibrariesOf(BaseItem item) => _library.GetCollectionFolders(item).Select(folder => folder.Id).ToHashSet();

    private Video? FindStub(string root, StubEntry stub) => _library.FindByPath(FederatedPaths.Resolve(root, stub.RelativePath), false) as Video;
}
