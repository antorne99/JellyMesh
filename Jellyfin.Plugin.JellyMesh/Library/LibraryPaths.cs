using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILibraryPaths"/> through <see cref="ILibraryManager.AddMediaPath"/> and <see cref="ILibraryManager.RemoveMediaPath"/>,
/// one call at a time.
/// </summary>
public sealed class LibraryPaths : ILibraryPaths
{
    private readonly ILibraryManager _library;
    private readonly ILogger<LibraryPaths> _logger;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryPaths"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public LibraryPaths(ILibraryManager library, ILogger<LibraryPaths> logger)
    {
        _library = library;
        _logger = logger;
    }

    /// <inheritdoc />
    public LibraryPathOutcome Add(string root, string library, string path)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (!IsLayoutFolder(root, path))
        {
            return LibraryPathOutcome.Refused;
        }

        lock (_lock)
        {
            var (folder, read) = Find(library);
            if (folder is null)
            {
                return read;
            }

            if (StoredAs(folder, path) is not null)
            {
                return LibraryPathOutcome.Unchanged;
            }

            try
            {
                _library.AddMediaPath(folder.Name, new MediaPathInfo(path));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The type only: a message could hold a path.
                _logger.LogError("Cannot add a folder of the federated folder to the library {Library}: {Error}", LogText.Clean(library), exception.GetType().Name);
                return LibraryPathOutcome.Failed;
            }

            return Find(library).Folder is { } after && StoredAs(after, path) is not null ? LibraryPathOutcome.Done : Silent(library);
        }
    }

    /// <inheritdoc />
    public LibraryPathOutcome Remove(string root, string library, string path)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (!IsLayoutFolder(root, path))
        {
            return LibraryPathOutcome.Refused;
        }

        lock (_lock)
        {
            var (folder, read) = Find(library);
            if (folder is null)
            {
                return read;
            }

            if (StoredAs(folder, path) is not { } stored)
            {
                return LibraryPathOutcome.Unchanged;
            }

            try
            {
                _library.RemoveMediaPath(folder.Name, stored);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError("Cannot remove a folder of the federated folder from the library {Library}: {Error}", LogText.Clean(library), exception.GetType().Name);
                return LibraryPathOutcome.Failed;
            }

            return Find(library).Folder is { } after && StoredAs(after, path) is null ? LibraryPathOutcome.Done : Silent(library);
        }
    }

    // The flat Film and Serie, and the Film and Serie of a server folder: nothing else of the admin's libraries.
    private static bool IsLayoutFolder(string? root, string? path)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (Same(path, FederatedFolderLayout.MoviesPath(root)) || Same(path, FederatedFolderLayout.SeriesPath(root)))
        {
            return true;
        }

        var parts = (FederatedPaths.RelativeTo(root, Path.TrimEndingDirectorySeparator(path)) ?? string.Empty).Split(Path.DirectorySeparatorChar);
        return parts.Length == 2
            && Guid.TryParseExact(parts[0], "D", out var server)
            && (Same(path, FederatedFolderLayout.ServerMoviesPath(root, server)) || Same(path, FederatedFolderLayout.ServerSeriesPath(root, server)));
    }

    private static bool Same(string location, string path) => FederatedFolderLinkChecker.IsSamePath(location, path);

    private static string? StoredAs(VirtualFolderInfo folder, string path) => (folder.Locations ?? []).FirstOrDefault(location => Same(location, path));

    private LibraryPathOutcome Silent(string library)
    {
        _logger.LogError("The library {Library} did not take the change of a folder of the federated folder", LogText.Clean(library));
        return LibraryPathOutcome.Failed;
    }

    // The library by its exact name, or the outcome that stops the call.
    private (VirtualFolderInfo? Folder, LibraryPathOutcome Outcome) Find(string library)
    {
        List<VirtualFolderInfo> folders;
        try
        {
            folders = _library.GetVirtualFolders();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot read the libraries to change a folder of the federated folder: {Error}", exception.GetType().Name);
            return (null, LibraryPathOutcome.Failed);
        }

        var found = folders.FirstOrDefault(folder => string.Equals(folder.Name, library, StringComparison.Ordinal));
        return (found, found is null ? LibraryPathOutcome.NoLibrary : LibraryPathOutcome.Done);
    }
}
