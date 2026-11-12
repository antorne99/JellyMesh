using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="ILibraryChangeTracker"/> kept in memory: after a restart the first look is the start.
/// </summary>
public sealed class LibraryChangeTracker : ILibraryChangeTracker
{
    private readonly ILibraryManager _libraryManager;
    private readonly IPluginConfigurationStore _store;
    private readonly ILogger<LibraryChangeTracker> _logger;
    private readonly object _lock = new();
    private Snapshot? _last;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeTracker"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager, only read.</param>
    /// <param name="store">The configuration, which holds the federated folder.</param>
    /// <param name="logger">The logger.</param>
    public LibraryChangeTracker(ILibraryManager libraryManager, IPluginConfigurationStore store, ILogger<LibraryChangeTracker> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Record()
    {
        var current = Read();
        lock (_lock)
        {
            _last = current ?? _last;
        }
    }

    /// <inheritdoc />
    public LibraryChanges Compare()
    {
        if (Read() is not { } current)
        {
            return LibraryChanges.None;
        }

        Snapshot last;
        lock (_lock)
        {
            if (_last is null || !string.Equals(_last.Root, current.Root, StringComparison.Ordinal))
            {
                _last = current;
                return LibraryChanges.None;
            }

            last = _last;
        }

        var added = current.Libraries.Where(library => !last.Libraries.ContainsKey(library.Key)).Select(library => library.Value).ToList();
        var removed = last.Libraries.Where(library => !current.Libraries.ContainsKey(library.Key)).Select(library => library.Value).ToList();
        return added.Count == 0 && removed.Count == 0 ? LibraryChanges.None : new LibraryChanges(added, removed);
    }

    // Null when there is no folder or the libraries cannot be read.
    private Snapshot? Read()
    {
        var root = _store.Read(config => config.FederatedFolderPath);
        if (root.Length == 0)
        {
            return null;
        }

        try
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(library => (library.Locations ?? [])
                    .Any(location => !string.IsNullOrWhiteSpace(location)
                        && (FederatedFolderLinkChecker.IsAncestorOrSame(location, root) || FederatedFolderLinkChecker.IsAncestorOrSame(root, location))))
                .GroupBy(library => string.IsNullOrEmpty(library.ItemId) ? library.Name : library.ItemId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
            return new Snapshot(root, libraries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot read the libraries to compare them with the last sync: {Error}", exception.GetType().Name);
            return null;
        }
    }

    private sealed record Snapshot(string Root, Dictionary<string, string> Libraries);
}
