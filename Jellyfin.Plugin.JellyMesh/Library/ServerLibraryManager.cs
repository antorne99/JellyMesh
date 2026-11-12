using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        var outcome = _store.Read(config => Find(config, serverId) is null
            ? AutomaticLibrariesOutcome.UnknownServer
            : config.FederatedFolderPath.Length == 0 ? AutomaticLibrariesOutcome.NoFederatedFolder : AutomaticLibrariesOutcome.Started);
        return outcome == AutomaticLibrariesOutcome.Started ? Start(serverId, EnableAsync) : AutomaticLibrariesRequest.Refused(outcome);
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
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
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
        finally
        {
            _gate.Release();
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

    private static string PathOf(ServerLibraryKind kind, string root, Guid serverId) => kind == ServerLibraryKind.Movies
        ? FederatedFolderLayout.ServerMoviesPath(root, serverId)
        : FederatedFolderLayout.ServerSeriesPath(root, serverId);

    private static CollectionTypeOptions TypeOf(ServerLibraryKind kind) => kind == ServerLibraryKind.Movies ? CollectionTypeOptions.movies : CollectionTypeOptions.tvshows;

    // The plugin acts on a library only while its one path is the server folder and its type is the expected one.
    private static bool Passes(VirtualFolderInfo folder, ServerLibraryKind kind, string root, Guid serverId) =>
        folder.CollectionType == TypeOf(kind)
        && folder.Locations is [var location]
        && FederatedFolderLinkChecker.IsSamePath(location, PathOf(kind, root, serverId));

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

    private async Task EnableAsync(Guid serverId, CancellationToken cancellationToken)
    {
        _store.Update(config =>
        {
            if (Find(config, serverId) is { } server)
            {
                server.AutomaticLibraries = true;
                server.ReleasedAutomaticLibraries.Clear();
            }
        });

        var folders = ReadFolders();
        if (folders is null)
        {
            _errors[serverId] = AutomaticLibrariesError.ReadFailed;
            return;
        }

        Follow(serverId, folders);
        _materializer.PrepareServerFolders();
        var missing = _store.Read(config => _kinds
            .Where(kind => Find(config, serverId)?.AutomaticLibraryRegistry.All(record => record.Kind != kind) == true)
            .ToList());
        if (missing.Count == 0)
        {
            return;
        }

        await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
        var created = false;
        foreach (var kind in missing)
        {
            created |= await CreateAsync(serverId, kind).ConfigureAwait(false);
        }

        if (created)
        {
            await _scans.QueueAndWaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> CreateAsync(Guid serverId, ServerLibraryKind kind)
    {
        var folders = ReadFolders();
        var (root, serverName) = _store.Read(config => (config.FederatedFolderPath, Find(config, serverId)?.Name));
        if (folders is null || root.Length == 0 || serverName is null)
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
        _store.Update(config => Find(config, serverId)?.AutomaticLibraryRegistry.Add(record));
        _logger.LogInformation("Created the library {Library} for {Server}", record.Name, serverName);
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
        while (_store.Read(config => Find(config, serverId)?.AutomaticLibraryRegistry.Count > 0))
        {
            await _scans.WaitIdleAsync(cancellationToken).ConfigureAwait(false);
            var folders = ReadFolders();
            var root = _store.Read(config => config.FederatedFolderPath);
            if (folders is null || root.Length == 0)
            {
                _errors[serverId] = AutomaticLibrariesError.ReadFailed;
                return;
            }

            // Checked again on a fresh read, so that the name is the current one and a changed library is left alone.
            Follow(serverId, folders);
            var next = _store.Read(config => Find(config, serverId)?.AutomaticLibraryRegistry.FirstOrDefault() is { } record
                ? new AutomaticLibraryStatus(record.Kind, record.Name)
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
                _errors[serverId] = AutomaticLibrariesError.RemoveFailed;
                return;
            }

            _store.Update(config =>
            {
                var registry = Find(config, serverId)?.AutomaticLibraryRegistry;
                if (registry?.FirstOrDefault(record => record.Kind == next.Kind) is { } record)
                {
                    registry.Remove(record);
                }
            });
            _logger.LogInformation("Deleted the library {Library}", next.Name);
        }

        _store.Update(config =>
        {
            if (Find(config, serverId) is { } server)
            {
                server.AutomaticLibraries = false;
            }
        });
    }

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

    // Follows a rename where path and type hold, and drops from the registry a library whose path or type changed.
    private void Follow(Guid serverId, IReadOnlyList<VirtualFolderInfo> folders)
    {
        var plan = _store.Read(config => Find(config, serverId) is { } server && config.FederatedFolderPath.Length > 0
            ? PlanFollow(server, config.FederatedFolderPath, folders)
            : []);
        if (plan.Count == 0)
        {
            return;
        }

        _store.Update(config =>
        {
            if (Find(config, serverId) is not { } server)
            {
                return;
            }

            foreach (var change in plan)
            {
                var record = server.AutomaticLibraryRegistry.FirstOrDefault(entry => entry.Kind == change.Kind);
                if (record is null)
                {
                    continue;
                }

                if (change.Found is { } found)
                {
                    record.Name = found.Name;
                    record.ItemId = found.ItemId ?? string.Empty;
                    continue;
                }

                server.AutomaticLibraryRegistry.Remove(record);
                if (!server.ReleasedAutomaticLibraries.Contains(change.ReleasedName, StringComparer.Ordinal))
                {
                    server.ReleasedAutomaticLibraries.Add(change.ReleasedName);
                }
            }
        });

        foreach (var change in plan.Where(change => change.Found is null))
        {
            _logger.LogWarning("The library {Library} changed or is gone, so it is left to the admin", change.ReleasedName);
        }
    }

    private static List<FollowChange> PlanFollow(RemoteServerConfig server, string root, IReadOnlyList<VirtualFolderInfo> folders)
    {
        // A library still known by id or name is the one in the registry, whatever its path now.
        var known = server.AutomaticLibraryRegistry
            .Select(record => (Record: record, Folder: FindRecorded(record, folders)))
            .ToList();
        var claimed = known.Where(pair => pair.Folder is not null).Select(pair => pair.Folder!).ToList();
        var changes = new List<FollowChange>();
        foreach (var (record, folder) in known)
        {
            if (folder is null)
            {
                // Gone by id and name: renamed if exactly one other library holds the same folder with the same type.
                var candidates = folders.Where(other => !claimed.Contains(other) && Passes(other, record.Kind, root, server.Id)).ToList();
                changes.Add(candidates.Count == 1
                    ? new FollowChange(record.Kind, candidates[0], record.Name)
                    : new FollowChange(record.Kind, null, record.Name));
            }
            else if (!Passes(folder, record.Kind, root, server.Id))
            {
                changes.Add(new FollowChange(record.Kind, null, folder.Name));
            }
            else if (!string.Equals(folder.Name, record.Name, StringComparison.Ordinal) || !string.Equals(folder.ItemId ?? string.Empty, record.ItemId, StringComparison.Ordinal))
            {
                changes.Add(new FollowChange(record.Kind, folder, record.Name));
            }
        }

        return changes;
    }

    // The id wins over the name. A library made later under an old name gets its id too, and is then checked by path.
    private static VirtualFolderInfo? FindRecorded(AutomaticLibraryConfig record, IReadOnlyList<VirtualFolderInfo> folders) =>
        (record.ItemId.Length > 0 ? folders.FirstOrDefault(folder => string.Equals(folder.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase)) : null)
        ?? folders.FirstOrDefault(folder => string.Equals(folder.Name, record.Name, StringComparison.Ordinal));

    private sealed record FollowChange(ServerLibraryKind Kind, VirtualFolderInfo? Found, string ReleasedName);
}
