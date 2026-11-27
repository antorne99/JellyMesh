using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// <see cref="IFederatedFolderLinkChecker"/> over the libraries of this server. Only reads them.
/// </summary>
/// <remarks>
/// The folders of a server are the type folders of its imported libraries, or its two 0.5 folders while titles are in that
/// layout. A path counts only if a library has exactly it: a library on a parent folder would index them as ordinary
/// content, and is reported as covering them. Paths compare as Jellyfin compares them, which is by exact case except on Windows.
/// </remarks>
public sealed class FederatedFolderLinkChecker : IFederatedFolderLinkChecker
{
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ILibraryManager _libraryManager;
    private readonly IPluginConfigurationStore _store;
    private readonly IStubRegistry _stubs;
    private readonly ILogger<FederatedFolderLinkChecker> _logger;
    private readonly ErrorLogLimits _errorLogs;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederatedFolderLinkChecker"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="store">The configuration, with the imported libraries of each server and the single libraries.</param>
    /// <param name="stubs">The stub registry, which tells the layout of the titles.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public FederatedFolderLinkChecker(ILibraryManager libraryManager, IPluginConfigurationStore store, IStubRegistry stubs, ILogger<FederatedFolderLinkChecker> logger, ErrorLogLimits? errorLogs = null)
    {
        _errorLogs = errorLogs ?? new ErrorLogLimits();
        _libraryManager = libraryManager;
        _store = store;
        _stubs = stubs;
        _logger = logger;
    }

    /// <summary>Gets the comparer of normalized paths.</summary>
    internal static StringComparer PathComparer => StringComparer.FromComparison(_pathComparison);

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
            _logger.LogError("Cannot read the libraries to check the federated folder: {Error}{Detail:l}", exception.GetType().Name, _errorLogs.Local.Of(exception));
            return FederatedFolderLinkReport.NotChecked;
        }

        var olderLayout = _stubs.UsesOlderLayout() ?? false;
        var (folders, shared) = _store.Read(config => (
            serverIds.Select(id => config.Servers.FirstOrDefault(server => server.Id == id) is { } server
                ? LibraryPlan.ManualPaths(server, federatedFolderPath, olderLayout)
                : []).ToList(),
            config.SharedLibraries.Libraries.Select(record => record.ItemId).Where(id => id.Length > 0).ToList()));

        // The single library holds every server, as it is meant to, so it is in no set that decides a split (D29).
        var counted = libraries.Where(library => !shared.Contains(library.ItemId ?? string.Empty, StringComparer.OrdinalIgnoreCase)).ToList();
        var links = serverIds.Select((server, index) => LinkOf(libraries, counted, server, folders[index])).ToList();
        var splits = SeriesSplits(counted, links);
        var servers = links.Select(link => link with { SeriesSplitWith = splits.GetValueOrDefault(link.ServerId) ?? [] }).ToList();
        var tops = serverIds.Select(server => FederatedFolderLayout.ServerPath(federatedFolderPath, server))
            .Concat(folders.SelectMany(own => own).Select(folder => Path.GetDirectoryName(Normalized(folder.Path))).OfType<string>())
            .Prepend(federatedFolderPath)
            .ToList();
        var covering = libraries
            .Where(library => Locations(library).Any(location => tops.Any(top => IsAncestorOrSame(location, top))))
            .Select(library => library.Name)
            .ToList();
        return new FederatedFolderLinkReport(servers, covering, splits.Count > 0);
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

    private static ServerFolderLink LinkOf(List<VirtualFolderInfo> libraries, List<VirtualFolderInfo> counted, Guid serverId, IReadOnlyList<ManualLibraryPath> folders)
    {
        var links = folders.Select(folder => new TypeFolderLink(folder.Path, folder.LibraryType, folder.RemoteLibrary, StatusOf(libraries, folder.Path, ExpectedType(folder)))).ToList();
        var series = folders.Where(folder => ExpectedType(folder) == CollectionTypeOptions.tvshows).Select(folder => folder.Path).ToList();
        var holdingSeries = libraries.Where(library => series.Any(path => Holds(library, path, _pathComparison))).ToList();
        var groupingOff = holdingSeries
            .Where(library => library.CollectionType == CollectionTypeOptions.tvshows && library.LibraryOptions is { EnableAutomaticSeriesGrouping: false })
            .Select(library => library.Name)
            .ToList();
        var musicInMixed = libraries
            .Where(library => library.CollectionType is null or CollectionTypeOptions.mixed
                && folders.Any(folder => ExpectedType(folder) == CollectionTypeOptions.music && Holds(library, folder.Path, _pathComparison)))
            .Select(library => library.Name)
            .ToList();
        var onlyOnItsFolders = libraries
            .Where(library => Locations(library).Any() && Locations(library).All(location => folders.Any(folder => IsSame(location, folder.Path))))
            .Select(library => library.Name)
            .ToList();
        return new ServerFolderLink(
            serverId,
            links,
            Together(links.Where(link => ExpectedType(link.LibraryType) == CollectionTypeOptions.movies)),
            Together(links.Where(link => ExpectedType(link.LibraryType) == CollectionTypeOptions.tvshows)),
            groupingOff,
            Names(holdingSeries.Where(counted.Contains).ToList()),
            onlyOnItsFolders)
        {
            MusicInMixed = musicInMixed
        };
    }

    // Series of two servers merge in a library only where their series folders sit in the same libraries (Q46,
    // R:prova §5): each server with the others whose folders share a library with its own in another set of libraries.
    private static Dictionary<Guid, List<Guid>> SeriesSplits(List<VirtualFolderInfo> counted, List<ServerFolderLink> servers)
    {
        var folders = servers
            .SelectMany(server => server.Folders.Where(folder => ExpectedType(folder.LibraryType) == CollectionTypeOptions.tvshows).Select(folder => (server.ServerId, folder.Path)))
            .Select(folder => (folder.ServerId, Libraries: counted.Where(library => Holds(library, folder.Path, _pathComparison)).ToHashSet()))
            .ToList();
        var pairs = folders
            .SelectMany(left => folders, (left, right) => (Left: left, Right: right))
            .Where(pair => pair.Left.ServerId != pair.Right.ServerId
                && pair.Left.Libraries.Overlaps(pair.Right.Libraries)
                && !pair.Left.Libraries.SetEquals(pair.Right.Libraries))
            .Select(pair => (pair.Left.ServerId, Other: pair.Right.ServerId));
        var order = servers.Select(server => server.ServerId).ToList();
        return pairs
            .GroupBy(pair => pair.ServerId, pair => pair.Other)
            .ToDictionary(group => group.Key, group => group.Distinct().OrderBy(order.IndexOf).ToList());
    }

    // The link of several folders of one kind: the worst of them, and linked when there are none.
    private static FederatedPathStatus Together(IEnumerable<TypeFolderLink> folders)
    {
        var statuses = folders.Select(folder => folder.Link).ToList();
        foreach (var link in new[] { FederatedPathLink.WrongLibraryType, FederatedPathLink.DifferentCase, FederatedPathLink.NotLinked })
        {
            if (statuses.Any(status => status.Link == link))
            {
                return new FederatedPathStatus(link, statuses.Where(status => status.Link == link).SelectMany(status => status.Libraries).Distinct().ToList());
            }
        }

        return new FederatedPathStatus(FederatedPathLink.Linked, statuses.SelectMany(status => status.Libraries).Distinct().ToList());
    }

    private static CollectionTypeOptions? ExpectedType(ManualLibraryPath folder) => ExpectedType(folder.LibraryType);

    private static CollectionTypeOptions? ExpectedType(string libraryType)
        => Enum.TryParse<CollectionTypeOptions>(libraryType, true, out var type) ? type : null;

    private static FederatedPathStatus StatusOf(List<VirtualFolderInfo> libraries, string path, CollectionTypeOptions? expected)
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

    private static bool IsAccepted(CollectionTypeOptions? type, CollectionTypeOptions? expected) =>
        type == expected || type is null or CollectionTypeOptions.mixed;

    private static List<string> Names(List<VirtualFolderInfo> libraries) => libraries.Select(library => library.Name).ToList();

    private static bool Holds(VirtualFolderInfo library, string path, StringComparison comparison)
    {
        var wanted = Normalized(path);
        return Locations(library).Any(location => string.Equals(Normalized(location), wanted, comparison));
    }

    private static IEnumerable<string> Locations(VirtualFolderInfo library) => (library.Locations ?? []).Where(IsPath);

    // A location that is no path, such as one with a null character, holds no folder and must not stop the check.
    internal static bool IsPath(string location)
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
    internal static string Normalized(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
