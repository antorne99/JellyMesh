using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Removes stubs, with the folders they leave empty and the matching library rows: a season or series left with no files
/// stays visible until its folder goes (Phase 0, point 2). Deletes only what the registry knows, inside the federated
/// folder, and the library rows go with <c>DeleteFileLocation</c> off so that Jellyfin touches no file by itself.
/// </summary>
public sealed class StubRemover
{
    private static readonly DeleteOptions _keepFiles = new() { DeleteFileLocation = false };

    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubRemover"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public StubRemover(ILibraryManager library)
    {
        _library = library;
    }

    /// <summary>
    /// Removes a stub.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="state">The registry.</param>
    /// <param name="stub">The stub.</param>
    /// <returns>The registry without the stub.</returns>
    /// <exception cref="IOException">A file or folder could not be deleted. The stub stays in the registry.</exception>
    /// <exception cref="UnauthorizedAccessException">A file or folder could not be deleted.</exception>
    public StubState Remove(string root, StubState state, StubEntry stub)
    {
        ArgumentNullException.ThrowIfNull(stub);

        var removal = RemoveAll(root, state, [stub]);
        if (removal.Failures.Count > 0)
        {
            ExceptionDispatchInfo.Capture(removal.Failures[0]).Throw();
        }

        return removal.State;
    }

    /// <summary>
    /// Removes many stubs in one pass, so that a mass removal stays linear. A stub that cannot be removed, or whose season or
    /// series folder cannot, stays in the registry and is tried again next time; the others go.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="state">The registry.</param>
    /// <param name="stubs">The stubs.</param>
    /// <returns>The registry without the stubs, with what failed.</returns>
    public StubRemoval RemoveAll(string root, StubState state, IReadOnlyCollection<StubEntry> stubs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stubs);

        var failures = new List<Exception>();
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stub in stubs)
        {
            if (Try(failures, () => DeleteStub(root, stub)))
            {
                gone.Add(stub.Id);
            }
        }

        var remaining = state.Stubs.Where(entry => !gone.Contains(entry.Id)).ToList();
        var usedFolders = remaining.Select(entry => Path.GetDirectoryName(entry.RelativePath)!).ToHashSet(StringComparer.Ordinal);
        var usedSeries = remaining.Select(entry => entry.SeriesStubId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var episodes = stubs.Where(stub => stub.Kind != StubKind.Movie && gone.Contains(stub.Id)).ToList();
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var season in episodes.GroupBy(stub => Path.GetDirectoryName(stub.RelativePath)!, StringComparer.Ordinal))
        {
            if (!usedFolders.Contains(season.Key) && !Try(failures, () => DeleteFolderAndRow(root, season.Key)))
            {
                kept.UnionWith(season.Select(stub => stub.Id));
            }
        }

        var owners = state.Series.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var goneSeries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var series in episodes.Where(stub => stub.SeriesStubId is not null).GroupBy(stub => stub.SeriesStubId!, StringComparer.Ordinal))
        {
            if (usedSeries.Contains(series.Key) || !owners.TryGetValue(series.Key, out var owner))
            {
                continue;
            }

            if (Try(failures, () => DeleteFolderAndRow(root, owner.RelativePath)))
            {
                goneSeries.Add(owner.Id);
            }
            else
            {
                kept.UnionWith(series.Select(stub => stub.Id));
            }
        }

        var next = state with
        {
            Stubs = state.Stubs.Where(entry => !gone.Contains(entry.Id) || kept.Contains(entry.Id)).ToList(),
            Series = state.Series.Where(entry => !goneSeries.Contains(entry.Id)).ToList()
        };
        return new StubRemoval(next, gone.Count(id => !kept.Contains(id)), failures);
    }

    /// <summary>
    /// Removes series that no stub uses any more, with their folders and items. A series still used, or whose folder
    /// cannot be removed, stays in the registry.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="state">The registry.</param>
    /// <param name="series">The series.</param>
    /// <returns>The registry without the series, with what failed.</returns>
    public StubRemoval RemoveSeries(string root, StubState state, IReadOnlyCollection<SeriesStub> series)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(series);

        var failures = new List<Exception>();
        var used = state.Stubs.Select(entry => entry.SeriesStubId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var gone = series
            .Where(entry => !used.Contains(entry.Id) && Try(failures, () => DeleteFolderAndRow(root, entry.RelativePath)))
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        return new StubRemoval(state with { Series = state.Series.Where(entry => !gone.Contains(entry.Id)).ToList() }, gone.Count, failures);
    }

    /// <summary>
    /// Deletes the folder of each server, with the series still in it, once no stub of that server is left. A server with a
    /// stub left, or whose series cannot be removed, keeps its folder and is tried again next time.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="state">The registry.</param>
    /// <param name="servers">The removed servers.</param>
    /// <returns>The registry without their series, with how many folders went and what failed.</returns>
    public StubRemoval RemoveServerFolders(string root, StubState state, IReadOnlyCollection<Guid> servers)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(servers);

        var failures = new List<Exception>();
        var removed = 0;
        foreach (var serverId in servers.Distinct())
        {
            if (state.Stubs.Any(stub => stub.ServerId == serverId))
            {
                continue;
            }

            var series = state.Series.Where(entry => entry.ServerId == serverId).ToList();
            if (series.Count > 0)
            {
                var removal = RemoveSeries(root, state, series);
                state = removal.State;
                failures.AddRange(removal.Failures);
                if (state.Series.Any(entry => entry.ServerId == serverId))
                {
                    continue;
                }
            }

            var folder = FederatedPaths.ServerFolder(serverId);
            if (Directory.Exists(FederatedPaths.Resolve(root, folder)) && Try(failures, () => DeleteFolder(root, folder)))
            {
                removed++;
            }
        }

        return new StubRemoval(state, removed, failures);
    }

    private static bool Try(List<Exception> failures, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(exception);
            return false;
        }
    }

    private void DeleteStub(string root, StubEntry stub)
    {
        var strm = FederatedPaths.Resolve(root, stub.RelativePath);
        DeleteRow(strm, false);
        if (stub.Kind == StubKind.Movie)
        {
            DeleteFolder(root, Path.GetDirectoryName(stub.RelativePath)!);
        }
        else
        {
            DeleteFilesOf(strm);
        }
    }

    private void DeleteFolderAndRow(string root, string relative)
    {
        DeleteRow(FederatedPaths.Resolve(root, relative), true);
        DeleteFolder(root, relative);
    }

    private static void DeleteFilesOf(string strm)
    {
        var folder = Path.GetDirectoryName(strm)!;
        if (!Directory.Exists(folder))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(strm);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(stem + ".", StringComparison.Ordinal) || name.StartsWith(stem + "-thumb.", StringComparison.Ordinal))
            {
                File.Delete(file);
            }
        }
    }

    private static void DeleteFolder(string root, string relative)
    {
        var folder = FederatedPaths.Resolve(root, relative);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private void DeleteRow(string path, bool isFolder)
    {
        if (_library.FindByPath(path, isFolder) is { } item)
        {
            _library.DeleteItem(item, _keepFiles);
        }
    }
}
