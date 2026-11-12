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
/// A path counts only if a library has exactly it: a library on a parent folder would index the server folders as
/// ordinary content, and is reported as covering them. Paths compare as Jellyfin compares them, which is by exact case except on Windows.
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
    public FederatedFolderLinkReport Check(string federatedFolderPath, IReadOnlyList<Guid> serverIds)
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

        var servers = serverIds.Select(server => LinkOf(libraries, federatedFolderPath, server)).ToList();
        var tops = serverIds.Select(server => FederatedFolderLayout.ServerPath(federatedFolderPath, server)).Prepend(federatedFolderPath).ToList();
        var covering = libraries
            .Where(library => Locations(library).Any(location => tops.Any(top => IsAncestorOrSame(location, top))))
            .Select(library => library.Name)
            .ToList();

        // Series of different servers merge only where their folders sit in the same libraries (Q46, R:prova §5).
        var seriesSets = servers.Where(server => server.SeriesLibraries.Count > 0).Select(server => server.SeriesLibraries).ToList();
        var split = seriesSets.Skip(1).Any(set => !set.ToHashSet().SetEquals(seriesSets[0]));
        return new FederatedFolderLinkReport(servers, covering, split);
    }

    /// <summary>Tells whether a library location is the given folder, as the link check compares them.</summary>
    /// <param name="location">The library location.</param>
    /// <param name="path">The folder.</param>
    /// <returns>Whether they are the same folder.</returns>
    internal static bool IsSamePath(string location, string path) => Holds(new VirtualFolderInfo { Locations = [location] }, path, _pathComparison);

    /// <summary>Tells whether a library location is the given folder or one of its parents, on a separator boundary.</summary>
    /// <param name="location">The library location.</param>
    /// <param name="path">The folder.</param>
    /// <returns>Whether the location holds the folder.</returns>
    internal static bool IsAncestorOrSame(string location, string path)
    {
        var folder = Normalized(location);
        var target = Normalized(path);
        if (string.Equals(folder, target, _pathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, _pathComparison);
    }

    private static ServerFolderLink LinkOf(List<VirtualFolderInfo> libraries, string root, Guid serverId)
    {
        var movies = FederatedFolderLayout.ServerMoviesPath(root, serverId);
        var series = FederatedFolderLayout.ServerSeriesPath(root, serverId);
        var holdingSeries = libraries.Where(library => Holds(library, series, _pathComparison)).ToList();
        var groupingOff = holdingSeries
            .Where(library => library.CollectionType == CollectionTypeOptions.tvshows && library.LibraryOptions is { EnableAutomaticSeriesGrouping: false })
            .Select(library => library.Name)
            .ToList();
        var onlyOnItsFolders = libraries
            .Where(library => Locations(library).Any() && Locations(library).All(location => IsSame(location, movies) || IsSame(location, series)))
            .Select(library => library.Name)
            .ToList();
        return new ServerFolderLink(
            serverId,
            movies,
            series,
            StatusOf(libraries, movies, CollectionTypeOptions.movies),
            StatusOf(libraries, series, CollectionTypeOptions.tvshows),
            groupingOff,
            Names(holdingSeries),
            onlyOnItsFolders);
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
        return Locations(library).Any(location => string.Equals(Normalized(location), wanted, comparison));
    }

    private static IEnumerable<string> Locations(VirtualFolderInfo library) => (library.Locations ?? []).Where(IsPath);

    // A location that is no path, such as one with a null character, holds no folder and must not stop the check.
    private static bool IsPath(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        try
        {
            _ = Path.GetFullPath(location);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSame(string location, string path) => string.Equals(Normalized(location), Normalized(path), _pathComparison);

    // Trailing separators, doubled ones and dots do not make another folder.
    private static string Normalized(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
