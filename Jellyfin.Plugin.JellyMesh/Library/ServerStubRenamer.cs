using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Moves the stubs of a renamed server to its new label with the mechanism of the migration (Q38): the new files are
/// written next to the old ones, and once a scan has made the new items the watch state is carried and the old files go.
/// </summary>
public sealed class ServerStubRenamer : IServerStubRenamer
{
    private static readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly StubRegistry _registry;
    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _library;
    private readonly IWatchStateCarrier _carrier;
    private readonly IVersionLinker _linker;
    private readonly ILogger<ServerStubRenamer> _logger;
    private readonly Action<string> _delete;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerStubRenamer"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="carrier">The watch state carrier.</param>
    /// <param name="linker">The version linker, which detaches an old item before its file goes.</param>
    /// <param name="logger">The logger.</param>
    public ServerStubRenamer(StubRegistry registry, IPluginConfigurationStore store, ILibraryManager library, IWatchStateCarrier carrier, IVersionLinker linker, ILogger<ServerStubRenamer> logger)
        : this(registry, store, library, carrier, linker, logger, File.Delete)
    {
    }

    // Takes how an old file is deleted, so that tests can make it fail.
    internal ServerStubRenamer(StubRegistry registry, IPluginConfigurationStore store, ILibraryManager library, IWatchStateCarrier carrier, IVersionLinker linker, ILogger<ServerStubRenamer> logger, Action<string> delete)
    {
        _registry = registry;
        _store = store;
        _library = library;
        _carrier = carrier;
        _linker = linker;
        _logger = logger;
        _delete = delete;
    }

    /// <inheritdoc />
    public bool IsPending(Guid serverId)
    {
        // An entry whose stub left the registry waits for no scan, and a relabel settles it first.
        return _registry.State.Renames.Any(rename => _registry.Find(rename.StubId) is { } stub && stub.ServerId == serverId);
    }

    /// <inheritdoc />
    public async Task<int> RelabelAsync(Guid serverId, string name, CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _registry.State;
            if (!state.Labels.TryGetValue(serverId, out var old))
            {
                // No stub was ever planned: the planner gives the label from the new name.
                return 0;
            }

            var label = FederatedPaths.Relabel(serverId, name, state.Labels);
            if (string.Equals(label, old, StringComparison.Ordinal))
            {
                return 0;
            }

            var stubs = state.Stubs.ToList();
            var paths = stubs.Select(stub => stub.ServerId == serverId ? NewPath(stub.RelativePath, old, label) : null).ToList();
            var live = LivePaths(state, paths.OfType<string>());

            // The old files of a stub that left go now, so that a file written at their path is not deleted later.
            var renames = state.Renames
                .Where(rename => !IsTaken(rename, live)
                    && (rename.RemovedItemId is not null || _registry.Find(rename.StubId) is not null || root.Length == 0 || !TryDeleteOld(root, rename, null)))
                .ToList();
            var written = new List<string>();
            var moved = 0;
            try
            {
                for (var index = 0; index < stubs.Count; index++)
                {
                    var stub = stubs[index];
                    if (paths[index] is not { } path)
                    {
                        continue;
                    }

                    if (root.Length > 0 && CopyFiles(root, stub.RelativePath, path, written))
                    {
                        renames.Add(new StubRename(stub.Id, stub.RelativePath));
                    }

                    // The new item has no URL or technical data yet: the next sync applies them.
                    stubs[index] = stub with { RelativePath = path, UrlTag = null, StrmWrittenUtc = null, TechnicalTag = null };
                    moved++;
                }

                var labels = new Dictionary<Guid, string>(state.Labels) { [serverId] = label };
                await _registry.SaveAsync(state with { Stubs = stubs, Renames = renames, Labels = labels }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                foreach (var file in written)
                {
                    TryDelete(file);
                }

                throw;
            }

            _logger.LogInformation("Moved {Count} stubs to the new label of a renamed server", moved);
            return moved;
        }
        finally
        {
            _registry.Exit();
        }
    }

    /// <inheritdoc />
    public async Task FinishAsync(CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0 || _registry.State.Renames.Count == 0)
        {
            return;
        }

        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _registry.State;
            var next = new Dictionary<StubRename, StubRename?>();
            var moves = new List<(StubRename Rename, StubEntry Stub, WatchStateMove Move)>();
            var forget = new List<Guid>();
            var live = LivePaths(state, []);
            foreach (var rename in state.Renames)
            {
                if (IsTaken(rename, live))
                {
                    next[rename] = null;
                    continue;
                }

                var stub = _registry.Find(rename.StubId);
                if (rename.RemovedItemId is { } removed)
                {
                    // Gone after a scan: the linker may link the new item, and Prune forgets what this call could not.
                    if (_library.GetItemById(removed) is null)
                    {
                        next[rename] = null;
                        if (rename.Forgets)
                        {
                            forget.Add(removed);
                        }
                    }

                    continue;
                }

                var old = stub is null ? null : ItemAt(root, rename.OldPath, stub.Kind);
                var renamed = stub is null ? null : ItemAt(root, stub.RelativePath, stub.Kind);
                if (stub is null || (old is null && renamed is not null))
                {
                    // Nothing to carry: the stub left the registry, or a scan made the new item but never the old one.
                    if (TryDeleteOld(root, rename, stub))
                    {
                        next[rename] = null;
                    }

                    continue;
                }

                // Neither item, as with the folder in no library: the old file keeps its state for the scan that brings it back.
                if (old is not null && renamed is not null)
                {
                    var from = stub with { RelativePath = rename.OldPath };
                    moves.Add((rename, stub, WatchStateMove.Of(from, old.Id, [new WatchStateTarget(renamed.Id, stub.ServerId)], removesOldItem: true)));
                }
            }

            if (moves.Count > 0)
            {
                var report = await _carrier.CarryAsync(moves.Select(entry => entry.Move).ToList(), cancellationToken).ConfigureAwait(false);
                var forgettable = report.ForgettableOldItemIds.ToHashSet();
                var done = report.Moves.Where(IsDone).Select(outcome => moves.First(entry => ReferenceEquals(entry.Move, outcome.Move))).ToList();
                var stuck = await StuckInGroupsAsync(done.Select(entry => entry.Rename).ToList(), cancellationToken).ConfigureAwait(false);
                foreach (var (rename, stub, move) in done.Where(entry => !stuck.Contains(entry.Rename)))
                {
                    if (TryDeleteOld(root, rename, stub))
                    {
                        next[rename] = rename with { RemovedItemId = move.FromItemId, Forgets = forgettable.Contains(move.FromItemId) };
                    }
                }
            }

            if (forget.Count > 0)
            {
                await _carrier.ForgetRemovedAsync(forget, cancellationToken).ConfigureAwait(false);
            }

            if (next.Count > 0)
            {
                var renames = state.Renames
                    .Select(rename => next.TryGetValue(rename, out var changed) ? changed : rename)
                    .OfType<StubRename>()
                    .ToList();
                await _registry.SaveAsync(state with { Renames = renames }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _registry.Exit();
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        await _registry.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _registry.State;
            var ours = state.Renames.Where(rename => _registry.Find(rename.StubId) is { } stub && stub.ServerId == serverId).ToHashSet();
            if (ours.Count == 0)
            {
                return true;
            }

            var live = state.Stubs.Select(stub => stub.RelativePath)
                .Concat(state.Renames.Where(rename => !ours.Contains(rename) && _registry.Find(rename.StubId) is not null).Select(rename => rename.OldPath))
                .ToHashSet(_pathComparer);
            var deleting = state.Renames
                .Where(rename => ours.Contains(rename) && rename.RemovedItemId is null && !live.Contains(rename.OldPath) && root.Length > 0)
                .ToList();
            var kept = await StuckInGroupsAsync(deleting, cancellationToken).ConfigureAwait(false);
            kept.UnionWith(deleting.Where(rename => !kept.Contains(rename) && !TryDeleteOld(root, rename, _registry.Find(rename.StubId))));
            var renames = state.Renames.Where(rename => !ours.Contains(rename) || kept.Contains(rename)).ToList();
            await _registry.SaveAsync(state with { Renames = renames }, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Cancelled {Count} pending stub moves of a renamed server, {Left} left", ours.Count - kept.Count, kept.Count);
            return kept.Count == 0;
        }
        finally
        {
            _registry.Exit();
        }
    }

    // The paths of live stubs, the old paths their renames still carry from, and the paths they are about to take.
    private HashSet<string> LivePaths(StubState state, IEnumerable<string> taking) =>
        state.Stubs.Select(stub => stub.RelativePath)
            .Concat(state.Renames.Where(rename => _registry.Find(rename.StubId) is not null).Select(rename => rename.OldPath))
            .Concat(taking)
            .ToHashSet(_pathComparer);

    // Detaches the old items of these moves before their files go (Q327), and returns the moves whose old item could not
    // leave its version group, which wait for the next round (Q326). The caller holds the registry lock.
    private async Task<HashSet<StubRename>> StuckInGroupsAsync(IReadOnlyList<StubRename> renames, CancellationToken cancellationToken)
    {
        var olds = renames
            .Select(rename => (Rename: rename, Stub: _registry.Find(rename.StubId)))
            .Where(entry => entry.Stub is not null)
            .Select(entry => (entry.Rename, Old: entry.Stub! with { RelativePath = entry.Rename.OldPath }))
            .ToList();
        if (olds.Count == 0)
        {
            return [];
        }

        var failed = await _linker.DetachForRemovalAsync(olds.Select(entry => entry.Old).ToList(), cancellationToken).ConfigureAwait(false);
        return olds.Where(entry => failed.Contains(entry.Old)).Select(entry => entry.Rename).ToHashSet();
    }

    // An entry whose stub left, at a path a live stub has or takes: its files and item ID are that stub's now.
    private bool IsTaken(StubRename rename, HashSet<string> live) => _registry.Find(rename.StubId) is null && live.Contains(rename.OldPath);

    // As the migration: done for every user, or left only for users who see no version (row 166).
    private static bool IsDone(WatchStateMoveOutcome outcome) =>
        outcome.Status == WatchStateMoveStatus.Carried
        || (outcome.Status == WatchStateMoveStatus.UsersLeft && outcome.SkippedUsers.All(user => user.Reason == WatchStateUserSkipReason.NoVisibleVersion));

    // The path with the new label, null for a stub whose name does not end with the old one.
    private static string? NewPath(string relative, string old, string label)
    {
        var suffix = " - " + old + ".strm";
        var name = Path.GetFileName(relative);
        return name.EndsWith(suffix, StringComparison.Ordinal)
            ? Path.Combine(Path.GetDirectoryName(relative) ?? string.Empty, name[..^suffix.Length] + " - " + label + ".strm")
            : null;
    }

    // The files of a stub: the .strm, and for an episode its .nfo and thumbnail, which share its stem.
    private static List<string> FilesOf(string strm, bool onlyStrm)
    {
        if (onlyStrm)
        {
            return File.Exists(strm) ? [strm] : [];
        }

        var folder = Path.GetDirectoryName(strm)!;
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var stem = Path.GetFileNameWithoutExtension(strm);
        return Directory.EnumerateFiles(folder)
            .Where(file => Path.GetFileName(file) is var name
                && (name.StartsWith(stem + ".", StringComparison.Ordinal) || name.StartsWith(stem + "-thumb.", StringComparison.Ordinal)))
            .ToList();
    }

    // Copies the files of a stub to its new stem. False when the old .strm is not on disk, so nothing waits for it.
    private static bool CopyFiles(string root, string oldPath, string newPath, List<string> written)
    {
        var from = FederatedPaths.Resolve(root, oldPath);
        if (!File.Exists(from))
        {
            return false;
        }

        var to = FederatedPaths.Resolve(root, newPath);
        var oldStem = Path.GetFileNameWithoutExtension(from);
        var newStem = Path.GetFileNameWithoutExtension(to);
        var folder = Path.GetDirectoryName(to)!;
        foreach (var file in FilesOf(from, false))
        {
            var target = Path.Combine(folder, newStem + Path.GetFileName(file)[oldStem.Length..]);
            File.Copy(file, target, overwrite: true);
            written.Add(target);
        }

        return true;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind: a later rename writes over it.
        }
    }

    // Like the version linker, by the ID Jellyfin gives the path, which finds an owned version too.
    private Video? ItemAt(string root, string relative, StubKind kind)
    {
        var path = FederatedPaths.Resolve(root, relative);
        var type = kind == StubKind.Episode ? typeof(Episode) : typeof(Movie);
        return _library.GetItemById(_library.GetNewItemId(path, type)) as Video
            ?? _library.GetItemById(_library.GetNewItemId(path, typeof(Video))) as Video;
    }

    // A film shares its folder, .nfo and images with nothing else of the old stub, so only its .strm goes.
    private bool TryDeleteOld(string root, StubRename rename, StubEntry? stub)
    {
        try
        {
            foreach (var file in FilesOf(FederatedPaths.Resolve(root, rename.OldPath), stub?.Kind == StubKind.Movie))
            {
                _delete(file);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Cannot delete the old file of a renamed stub: {Error}", exception.GetType().Name);
            return false;
        }
    }
}
