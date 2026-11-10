using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IFederatedFolderLinkChecker"/> over the libraries of this server. Only reads them.
/// </summary>
/// <remarks>
/// A path counts only if a library has exactly it: a library on a parent folder would index <c>Film</c> and <c>Serie</c>
/// as ordinary content. Paths compare as Jellyfin compares them, which is by exact case except on Windows.
/// </remarks>
public sealed class FederatedFolderLinkChecker : IFederatedFolderLinkChecker
{
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<FederatedFolderLinkChecker> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederatedFolderLinkChecker"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public FederatedFolderLinkChecker(ILibraryManager libraryManager, ILogger<FederatedFolderLinkChecker> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public FederatedFolderLinkReport Check(string federatedFolderPath)
    {
        List<VirtualFolderInfo> libraries;
        try
        {
            libraries = _libraryManager.GetVirtualFolders();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError("Cannot read the libraries to check the federated folder: {Error}", exception.GetType().Name);
            return FederatedFolderLinkReport.NotChecked;
        }

        var series = FederatedFolderLayout.SeriesPath(federatedFolderPath);
        var groupingOff = libraries
            .Where(library => library.CollectionType == CollectionTypeOptions.tvshows
                && library.LibraryOptions is { EnableAutomaticSeriesGrouping: false }
                && Holds(library, series, _pathComparison))
            .Select(library => library.Name)
            .ToList();

        return new FederatedFolderLinkReport(
            StatusOf(libraries, FederatedFolderLayout.MoviesPath(federatedFolderPath), CollectionTypeOptions.movies),
            StatusOf(libraries, series, CollectionTypeOptions.tvshows),
            groupingOff);
    }

    private static FederatedPathStatus StatusOf(List<VirtualFolderInfo> libraries, string path, CollectionTypeOptions expected)
    {
        var holding = libraries.Where(library => Holds(library, path, _pathComparison)).ToList();

        // A mixed library is the user's choice, so it counts as the right type.
        var wrong = holding.Where(library => !IsAccepted(library.CollectionType, expected)).ToList();
        if (wrong.Count > 0)
        {
            return new FederatedPathStatus(FederatedPathLink.WrongLibraryType, Names(wrong));
        }

        if (holding.Count > 0)
        {
            return new FederatedPathStatus(FederatedPathLink.Linked, Names(holding));
        }

        var otherCase = libraries.Where(library => Holds(library, path, StringComparison.OrdinalIgnoreCase)).ToList();
        return otherCase.Count > 0
            ? new FederatedPathStatus(FederatedPathLink.DifferentCase, Names(otherCase))
            : new FederatedPathStatus(FederatedPathLink.NotLinked, []);
    }

    private static bool IsAccepted(CollectionTypeOptions? type, CollectionTypeOptions expected) =>
        type == expected || type is null or CollectionTypeOptions.mixed;

    private static List<string> Names(List<VirtualFolderInfo> libraries) => libraries.Select(library => library.Name).ToList();

    private static bool Holds(VirtualFolderInfo library, string path, StringComparison comparison)
    {
        var wanted = Normalized(path);
        return library.Locations.Any(location => !string.IsNullOrWhiteSpace(location) && string.Equals(Normalized(location), wanted, comparison));
    }

    // Trailing separators, doubled ones and dots do not make another folder.
    private static string Normalized(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
