using System;
using System.IO;
using System.Linq;
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
    public StubState Remove(string root, StubState state, StubEntry stub)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stub);

        var strm = FederatedPaths.Resolve(root, stub.RelativePath);
        DeleteRow(strm, false);

        var remaining = state.Stubs.Where(entry => !string.Equals(entry.Id, stub.Id, StringComparison.Ordinal)).ToList();
        var folder = Path.GetDirectoryName(stub.RelativePath)!;
        var series = state.Series.ToList();

        if (stub.Kind == StubKind.Movie)
        {
            DeleteFolder(root, folder);
        }
        else
        {
            DeleteFilesOf(strm);
            if (!remaining.Exists(entry => string.Equals(Path.GetDirectoryName(entry.RelativePath), folder, StringComparison.Ordinal)))
            {
                DeleteRow(FederatedPaths.Resolve(root, folder), true);
                DeleteFolder(root, folder);
            }

            if (!remaining.Exists(entry => string.Equals(entry.SeriesStubId, stub.SeriesStubId, StringComparison.Ordinal))
                && series.Find(entry => string.Equals(entry.Id, stub.SeriesStubId, StringComparison.Ordinal)) is { } owner)
            {
                DeleteRow(FederatedPaths.Resolve(root, owner.RelativePath), true);
                DeleteFolder(root, owner.RelativePath);
                series.Remove(owner);
            }
        }

        return state with { Stubs = remaining, Series = series };
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
