using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Keeps a Film and a Serie library for each server whose switch is on, and acts only on the libraries in its registry
/// whose only path is still the server folder and whose type is still the expected one.
/// </summary>
public sealed class ServerLibraryManager : IServerLibraryManager, IDisposable
{
    private static readonly ServerLibraryKind[] _kinds = [ServerLibraryKind.Movies, ServerLibraryKind.Series];

    private readonly IPluginConfigurationStore _store;
    private readonly ILibraryManager _libraries;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryScans _scans;
    private readonly ILibraryMaterializer _materializer;
    private readonly ILogger<ServerLibraryManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _jobsLock = new();
    private readonly Dictionary<Guid, Task> _jobs = [];
    private readonly ConcurrentDictionary<Guid, AutomaticLibrariesError> _errors = new();
    private Task _cleanup = Task.CompletedTask;
    private bool _cleaning;
    private bool _cleanAgain;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerLibraryManager"/> class.
    /// </summary>
    /// <param name="store">The configuration store.</param>
    /// <param name="libraries">The library manager.</param>
    /// <param name="fileSystem">The file system, which gives the name the server will store.</param>
    /// <param name="scans">The library scans.</param>
    /// <param name="materializer">The materializer, which writes the server folders.</param>
    /// <param name="logger">The logger.</param>
    public ServerLibraryManager(
        IPluginConfigurationStore store,
        ILibraryManager libraries,
        IFileSystem fileSystem,
        ILibraryScans scans,
        ILibraryMaterializer materializer,
        ILogger<ServerLibraryManager> logger)
    {
        _store = store;
        _libraries = libraries;
        _fileSystem = fileSystem;
        _scans = scans;
        _materializer = materializer;
        _logger = logger;
    }

    /// <inheritdoc />
    public AutomaticLibrariesState GetState(Guid serverId)
    {
        var state = _store.Read(config => Find(config, serverId) is { } server
            ? new AutomaticLibrariesState(
                server.AutomaticLibraries,
                false,
                server.AutomaticLibraryRegistry.Select(record => new AutomaticLibraryStatus(record.Kind, record.Name)).ToList(),
                server.ReleasedAutomaticLibraries.ToList(),
                AutomaticLibrariesError.None)
            : new AutomaticLibrariesState(false, false, [], [], AutomaticLibrariesError.None));
        return state with { Busy = IsBusy(serverId), Error = _errors.GetValueOrDefault(serverId) };
    }

    /// <inheritdoc />
    public AutomaticLibrariesRequest Enable(Guid serverId)
    {
        // The job is bound to this server: one removed and added back with the same id is another (decision 116).
        var (outcome, owner) = _store.Read(config => Find(config, serverId) is not { } server
            ? (AutomaticLibrariesOutcome.UnknownServer, null)
            : (config.FederatedFolderPath.Length == 0 ? AutomaticLibrariesOutcome.NoFederatedFolder : AutomaticLibrariesOutcome.Started, server));
        return outcome == AutomaticLibrariesOutcome.Started && owner is not null
            ? Start(serverId, (id, token) => EnableAsync(id, owner, token))
            : AutomaticLibrariesRequest.Refused(outcome);
    }

    /// <inheritdoc />
    public AutomaticLibrariesRequest Disable(Guid serverId, bool confirmed)
    {
        var names = _store.Read(config => Find(config, serverId)?.AutomaticLibraryRegistry.Select(record => record.Name).ToList());
        if (names is null)
        {
            return AutomaticLibrariesRequest.Refused(AutomaticLibrariesOutcome.UnknownServer);
        }

        if (IsBusy(serverId))
        {
            return AutomaticLibrariesRequest.Refused(AutomaticLibrariesOutcome.Busy);
        }

        return names.Count > 0 && !confirmed
            ? new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.ConfirmationRequired, Task.CompletedTask, names)
            : Start(serverId, DisableAsync);
    }

    /// <inheritdoc />
    public Task DeleteRemovedServerLibraries()
    {
        lock (_jobsLock)
        {
            if (_cleaning)
            {
                _cleanAgain = true;
                return _cleanup;
            }

            _cleaning = true;
            _cleanup = Task.Run(CleanAsync);
            return _cleanup;
        }
    }

    /// <inheritdoc />
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                FollowAll();
            }
            finally
            {
                _gate.Release();
            }
        }

        // After a failure or a restart, what removed servers left is tried again.
        if (_store.Read(config => config.RemovedServerLibraries.Any(HasWork)))
        {
            _ = DeleteRemovedServerLibraries();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _gate.Dispose();
    }

    private static RemoteServerConfig? Find(PluginConfiguration config, Guid serverId) => config.Servers.FirstOrDefault(server => server.Id == serverId);

    // The server only while it is still the instance a job started for.
    private static RemoteServerConfig? Owned(PluginConfiguration config, RemoteServerConfig owner) =>
        config.Servers.FirstOrDefault(server => ReferenceEquals(server, owner));

    private static RemovedServerLibrariesConfig? HeldOf(PluginConfiguration config, Guid serverId) =>
        config.RemovedServerLibraries.FirstOrDefault(held => held.ServerId == serverId);

    // An entry left only with released names stays for the warning (decision 158), and the cleanup skips it.
    private static bool HasWork(RemovedServerLibrariesConfig held) => held.Libraries.Count > 0 || held.ReleasedLibraries.Count == 0;

    private static HashSet<string> IdSet(IEnumerable<string> ids) => ids.Where(id => id.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The registry of a configured server. It never adopts a library that a removed server with the same id left.
    private static Registry? LiveRegistry(PluginConfiguration config, Guid serverId) => Find(config, serverId) is { } server
        ? new Registry(
            serverId,
            server.AutomaticLibraryRegistry,
            server.ReleasedAutomaticLibraries,
            IdSet(server.PreexistingLibraryIds),
            IdSet(HeldOf(config, serverId)?.Libraries.Select(record => record.ItemId) ?? []),
            server.FollowedLibraryIds,
            true)
        : null;

    // The registry a removed server left. It never sees the libraries of a configured server with the same id, nor those
    // any configured server found at switch-on (decision 175). One it drops for a new federated folder is released (158).
    private static Registry? HeldRegistry(PluginConfiguration config, Guid serverId) => HeldOf(config, serverId) is { } held
        ? new Registry(
            serverId,
            held.Libraries,
            held.ReleasedLibraries,
            IdSet(held.PreexistingLibraryIds
                .Concat(Find(config, serverId)?.AutomaticLibraryRegistry.Select(record => record.ItemId) ?? [])
                .Concat(config.Servers.SelectMany(server => server.PreexistingLibraryIds))),
            IdSet([]),
            held.FollowedLibraryIds,
            false)
        : null;

    private static string PathOf(ServerLibraryKind kind, string root, Guid serverId) => kind == ServerLibraryKind.Movies
        ? FederatedFolderLayout.ServerMoviesPath(root, serverId)
        : FederatedFolderLayout.ServerSeriesPath(root, serverId);

    private static CollectionTypeOptions TypeOf(ServerLibraryKind kind) => kind == ServerLibraryKind.Movies ? CollectionTypeOptions.movies : CollectionTypeOptions.tvshows;

    // The plugin acts on a library only while its one path is the server folder and its type is the expected one.
    private static bool Passes(VirtualFolderInfo folder, ServerLibraryKind kind, string root, Guid serverId) =>
        folder.CollectionType == TypeOf(kind)
        && folder.Locations is [var location]
        && FederatedFolderLinkChecker.IsSamePath(location, PathOf(kind, root, serverId));

    // A library left only because the federated folder changed: it still holds the server folder under another root.
    private static bool PassesUnderOtherRoot(VirtualFolderInfo folder, ServerLibraryKind kind, Guid serverId) =>
        folder.Locations is [var location]
        && Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(location))) is { Length: > 0 } other
        && Passes(folder, kind, other, serverId);

    private static LibraryOptions OptionsFor(string path) => new()
    {
        PathInfos = [new MediaPathInfo(path)],
        MetadataSavers = [],
        SaveLocalMetadata = false,
        EnableRealtimeMonitor = false,
        EnableAutomaticSeriesGrouping = true,
        DisabledLocalMetadataReaders = [],
        EnableChapterImageExtraction = false,
        EnableTrickplayImageExtraction = false,
        EnableLUFSScan = false,
        AutomaticallyAddToCollection = false,
        TypeOptions = []
    };

    private bool IsBusy(Guid serverId)
    {
        lock (_jobsLock)
        {
            return _jobs.ContainsKey(serverId);
        }
    }

    // The lock is held while the job is stored, so that its own removal cannot run first.
    private AutomaticLibrariesRequest Start(Guid serverId, Func<Guid, CancellationToken, Task> work)
    {
        lock (_jobsLock)
        {
            if (_jobs.ContainsKey(serverId))
            {
                return AutomaticLibrariesRequest.Refused(AutomaticLibrariesOutcome.Busy);
            }

            var job = Task.Run(() => RunAsync(serverId, work));
            _jobs[serverId] = job;
            return new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.Started, job, []);
        }
    }

    private async Task RunAsync(Guid serverId, Func<Guid, CancellationToken, Task> work)
    {
        try
        {
            var token = _shutdown.Token;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _errors[serverId] = AutomaticLibrariesError.None;
                await work(serverId, token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down.
        }
        catch (Exception exception)
        {
            _logger.LogError("Cannot update the libraries of a server: {Error}", exception.GetType().Name);
            _errors[serverId] = AutomaticLibrariesError.Failed;
        }
        finally
        {
            lock (_jobsLock)
            {
                _jobs.Remove(serverId);
            }
        }
    }

    private async Task EnableAsync(Guid serverId, RemoteServerConfig owner, CancellationToken cancellationToken)
    {
        var live = false;
        _store.Update(config =>
        {
            if (Owned(config, owner) is { } server)
            {
                server.AutomaticLibraries = true;
                server.ReleasedAutomaticLibraries.Clear();
                live = true;
            }
        });
        if (!live)
        {
            return;
        }

        var folders = ReadFolders();
        if (folders is null)
        {
            _errors[serverId] = AutomaticLibrariesError.ReadFailed;
            return;
        }

        Follow(config => Owned(config, owner) is null ? null : LiveRegistry(config, serverId), folders);
        RecordPreexisting(serverId, owner, folders);
        _materializer.PrepareServerFolders();
        var missing = _store.Read(config => _kinds
            .Where(kind => Owned(config, owner)?.AutomaticLibraryRegistry.All(record => record.Kind != kind) == true)
            .ToList());
        if (missing.Count == 0)
        {
            return;
        }

        await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
        var created = false;
        foreach (var kind in missing)
        {
            created |= await CreateAsync(serverId, owner, kind).ConfigureAwait(false);
        }

        if (created)
        {
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> CreateAsync(Guid serverId, RemoteServerConfig owner, ServerLibraryKind kind)
    {
        var folders = ReadFolders();
        var (root, serverName) = _store.Read(config => (config.FederatedFolderPath, Owned(config, owner)?.Name));
        if (serverName is null)
        {
            // Removed meanwhile: nothing more is made for it, and a server added back is not told of this job.
            return false;
        }

        if (folders is null || root.Length == 0)
        {
            _errors[serverId] = AutomaticLibrariesError.ReadFailed;
            return false;
        }

        var name = ChooseName(serverName, serverId, kind, folders);
        if (name is null)
        {
            _logger.LogWarning("Cannot create the {Kind} library of {Server}: its names are taken", kind, serverName);
            _errors[serverId] = AutomaticLibrariesError.NameTaken;
            return false;
        }

        var path = PathOf(kind, root, serverId);
        try
        {
            await _libraries.AddVirtualFolder(name, TypeOf(kind), OptionsFor(path), false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Cannot create the {Kind} library of {Server}: {Error}", kind, serverName, exception.GetType().Name);
            _errors[serverId] = AutomaticLibrariesError.CreateFailed;
            return false;
        }

        // The server may add a number to the name, so the new library is found by its path among those not there before.
        var before = folders.Select(folder => folder.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var made = ReadFolders()?.FirstOrDefault(folder => !before.Contains(folder.Name) && Passes(folder, kind, root, serverId));
        var record = new AutomaticLibraryConfig { Kind = kind, Name = made?.Name ?? name, ItemId = made?.ItemId ?? string.Empty };
        var held = false;
        _store.Update(config =>
        {
            // A server removed meanwhile, even if added back, leaves the library to delete after it, in the same save (decision 140).
            if (Owned(config, owner) is { } server)
            {
                server.AutomaticLibraryRegistry.Add(record);
            }
            else
            {
                config.HoldAutomaticLibraries(serverId, [record], [], owner.FollowedLibraryIds);
                held = true;
            }
        });
        _logger.LogInformation("Created the library {Library} for {Server}", record.Name, serverName);
        if (held)
        {
            _ = DeleteRemovedServerLibraries();
        }

        return true;
    }

    // Like the label of a server, a taken name gets the first digits of the server GUID.
    private string? ChooseName(string serverName, Guid serverId, ServerLibraryKind kind, IReadOnlyList<VirtualFolderInfo> folders)
    {
        var suffix = kind == ServerLibraryKind.Movies ? "Film" : "Serie";
        var taken = folders.Select(folder => folder.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] candidates = [$"{serverName} - {suffix}", $"{serverName} {FederatedPaths.GuidStart(serverId)} - {suffix}"];
        return candidates
            .Select(candidate => _fileSystem.GetValidFilename(candidate).Trim())
            .FirstOrDefault(stored => stored.Length > 0 && !taken.Contains(stored));
    }

    private async Task DisableAsync(Guid serverId, CancellationToken cancellationToken)
    {
        if (!await DeleteAllAsync(config => LiveRegistry(config, serverId), serverId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _store.Update(config =>
        {
            if (Find(config, serverId) is { } server)
            {
                server.AutomaticLibraries = false;
            }
        });
    }

    private async Task CleanAsync()
    {
        while (true)
        {
            try
            {
                var token = _shutdown.Token;
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await DeleteHeldAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // The server is shutting down.
            }
            catch (Exception exception)
            {
                _logger.LogError("Cannot delete the libraries of a removed server: {Error}", exception.GetType().Name);
            }

            lock (_jobsLock)
            {
                if (!_cleanAgain)
                {
                    _cleaning = false;
                    return;
                }

                _cleanAgain = false;
            }
        }
    }

    // A removed server whose deletion stops keeps what is left, for the next try.
    private async Task DeleteHeldAsync(CancellationToken cancellationToken)
    {
        var serverIds = _store.Read(config => config.RemovedServerLibraries.Where(HasWork).Select(held => held.ServerId).ToList());
        foreach (var serverId in serverIds)
        {
            if (!await DeleteAllAsync(config => HeldRegistry(config, serverId), null, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            _store.Update(config =>
            {
                if (HeldOf(config, serverId) is { Libraries.Count: 0, ReleasedLibraries.Count: 0 } held)
                {
                    config.RemovedServerLibraries.Remove(held);
                }
            });
        }
    }

    // Deletes the libraries of a registry one at a time, each checked again on a fresh read. False when it had to stop.
    private async Task<bool> DeleteAllAsync(Func<PluginConfiguration, Registry?> locate, Guid? errorKey, CancellationToken cancellationToken)
    {
        while (_store.Read(config => locate(config)?.Libraries.Count > 0))
        {
            await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
            var folders = ReadFolders();
            var root = _store.Read(config => config.FederatedFolderPath);
            if (folders is null || root.Length == 0)
            {
                SetError(errorKey, AutomaticLibrariesError.ReadFailed);
                return false;
            }

            // Checked again on a fresh read, so that the name is the current one and a changed library is left alone.
            Follow(locate, folders);
            var next = _store.Read(config => locate(config) is { } registry && registry.Libraries.FirstOrDefault() is { } record
                ? new DeletedLibrary(registry.ServerId, record.Kind, record.Name)
                : null);
            if (next is null)
            {
                break;
            }

            try
            {
                await _libraries.RemoveVirtualFolder(next.Name, true).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError("Cannot delete the library {Library}: {Error}", next.Name, exception.GetType().Name);
                SetError(errorKey, AutomaticLibrariesError.RemoveFailed);
                return false;
            }

            // The server may have been removed meanwhile, so its record may now be among those it left.
            _store.Update(config =>
            {
                foreach (var registry in new[] { LiveRegistry(config, next.ServerId), HeldRegistry(config, next.ServerId) })
                {
                    if (registry?.Libraries.FirstOrDefault(record => record.Kind == next.Kind && string.Equals(record.Name, next.Name, StringComparison.Ordinal)) is { } record)
                    {
                        registry.Libraries.Remove(record);
                    }
                }
            });
            _logger.LogInformation("Deleted the library {Library}", next.Name);
        }

        return true;
    }

    private void SetError(Guid? serverId, AutomaticLibrariesError error)
    {
        if (serverId is { } key)
        {
            _errors[key] = error;
        }
    }

    // Called after the follow, so that a library of the registry renamed in the dashboard is not recorded as the admin's.
    private void RecordPreexisting(Guid serverId, RemoteServerConfig owner, IReadOnlyList<VirtualFolderInfo> folders) => _store.Update(config =>
    {
        if (Owned(config, owner) is not { } server || LiveRegistry(config, serverId) is not { } registry)
        {
            return;
        }

        var visible = registry.Visible(folders);
        var owned = registry.Libraries.Select(record => FindRecorded(record, visible)).OfType<VirtualFolderInfo>().ToList();
        var preexisting = folders
            .Where(folder => !owned.Contains(folder) && !string.IsNullOrEmpty(folder.ItemId))
            .Select(folder => folder.ItemId!)
            .ToList();
        server.PreexistingLibraryIds.Clear();
        foreach (var id in preexisting)
        {
            server.PreexistingLibraryIds.Add(id);
        }
    });

    private List<VirtualFolderInfo>? ReadFolders()
    {
        try
        {
            return _libraries.GetVirtualFolders();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Cannot read the libraries of the server: {Error}", exception.GetType().Name);
            return null;
        }
    }

    private void FollowAll()
    {
        var serverIds = _store.Read(config => config.FederatedFolderPath.Length == 0
            ? []
            : config.Servers.Where(server => server.AutomaticLibraryRegistry.Count > 0).Select(server => server.Id).ToList());
        if (serverIds.Count == 0)
        {
            return;
        }

        var folders = ReadFolders();
        if (folders is null)
        {
            return;
        }

        foreach (var serverId in serverIds)
        {
            Follow(serverId, folders);
        }
    }

    private void Follow(Guid serverId, IReadOnlyList<VirtualFolderInfo> folders) => Follow(config => LiveRegistry(config, serverId), folders);

    // Follows a rename where path and type hold, and drops from the registry a library whose path or type changed.
    // Then records the libraries there now, which a later check never takes for a renamed one (decision 157).
    private void Follow(Func<PluginConfiguration, Registry?> locate, IReadOnlyList<VirtualFolderInfo> folders)
    {
        var present = IdSet(folders.Select(folder => folder.ItemId ?? string.Empty));
        var (plan, seenChanged) = _store.Read(config => locate(config) is { } registry
            ? (config.FederatedFolderPath.Length > 0 ? PlanFollow(registry, config.FederatedFolderPath, folders) : [], !IdSet(registry.Followed).SetEquals(present))
            : ([], false));
        if (plan.Count == 0 && !seenChanged)
        {
            return;
        }

        _store.Update(config =>
        {
            if (locate(config) is not { } registry)
            {
                return;
            }

            registry.Followed.Clear();
            foreach (var id in present)
            {
                registry.Followed.Add(id);
            }

            foreach (var change in plan)
            {
                if (!registry.Libraries.Contains(change.Record))
                {
                    continue;
                }

                if (change.Found is { } found)
                {
                    change.Record.Name = found.Name;
                    change.Record.ItemId = found.ItemId ?? string.Empty;
                    continue;
                }

                registry.Libraries.Remove(change.Record);
                if (registry.Released is { } released
                    && (registry.ReleasesAll || change.RootChanged)
                    && !released.Contains(change.ReleasedName, StringComparer.Ordinal))
                {
                    released.Add(change.ReleasedName);
                }
            }
        });

        foreach (var change in plan.Where(change => change.Found is null))
        {
            _logger.LogWarning("The library {Library} changed or is gone, so it is left to the admin", change.ReleasedName);
        }
    }

    private static List<FollowChange> PlanFollow(Registry registry, string root, IReadOnlyList<VirtualFolderInfo> folders)
    {
        // A library still known by id or name is the one in the registry, whatever its path now.
        folders = registry.Visible(folders);
        var known = registry.Libraries
            .Select(record => (Record: record, Folder: FindRecorded(record, folders)))
            .ToList();
        var claimed = known.Where(pair => pair.Folder is not null).Select(pair => pair.Folder!).ToList();
        var changes = new List<FollowChange>();
        foreach (var (record, folder) in known)
        {
            if (folder is null)
            {
                // Gone by id and name: renamed if exactly one other library holds the same folder with the same type.
                var candidates = folders
                    .Where(other => !claimed.Contains(other) && registry.MayAdopt(other) && Passes(other, record.Kind, root, registry.ServerId))
                    .ToList();
                changes.Add(candidates.Count == 1
                    ? new FollowChange(record, candidates[0], record.Name, false)
                    : new FollowChange(record, null, record.Name, false));
            }
            else if (!Passes(folder, record.Kind, root, registry.ServerId))
            {
                changes.Add(new FollowChange(record, null, folder.Name, PassesUnderOtherRoot(folder, record.Kind, registry.ServerId)));
            }
            else if (!string.Equals(folder.Name, record.Name, StringComparison.Ordinal) || !string.Equals(folder.ItemId ?? string.Empty, record.ItemId, StringComparison.Ordinal))
            {
                changes.Add(new FollowChange(record, folder, record.Name, false));
            }
        }

        return changes;
    }

    // The id wins over the name. A library made later under an old name gets its id too, and is then checked by path.
    private static VirtualFolderInfo? FindRecorded(AutomaticLibraryConfig record, IReadOnlyList<VirtualFolderInfo> folders) =>
        (record.ItemId.Length > 0 ? folders.FirstOrDefault(folder => string.Equals(folder.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase)) : null)
        ?? folders.FirstOrDefault(folder => string.Equals(folder.Name, record.Name, StringComparison.Ordinal));

    private sealed record FollowChange(AutomaticLibraryConfig Record, VirtualFolderInfo? Found, string ReleasedName, bool RootChanged);

    private sealed record DeletedLibrary(Guid ServerId, ServerLibraryKind Kind, string Name);

    // A registry with the ids it never sees (libraries there at switch-on), never adopts, and saw at its last check.
    // A live one lists every library it drops as released, a held one only those left by a new federated folder.
    private sealed record Registry(
        Guid ServerId,
        Collection<AutomaticLibraryConfig> Libraries,
        Collection<string>? Released,
        IReadOnlySet<string> Hidden,
        IReadOnlySet<string> NotAdopted,
        Collection<string> Followed,
        bool ReleasesAll)
    {
        public List<VirtualFolderInfo> Visible(IReadOnlyList<VirtualFolderInfo> folders) =>
            folders.Where(folder => folder.ItemId is null || !Hidden.Contains(folder.ItemId)).ToList();

        // Only a library new since the last check may be a renamed one, so one with no id never is.
        public bool MayAdopt(VirtualFolderInfo folder) =>
            folder.ItemId is { Length: > 0 } id && !NotAdopted.Contains(id) && !Followed.Contains(id, StringComparer.OrdinalIgnoreCase);
    }
}
