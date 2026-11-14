using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// Adding and removing the federated folders in the admin's libraries (Q56, §10 rows 77 and 85): every call checks the
// libraries first and after, and takes a path only as the library stores it.
public sealed class LibraryPathsTests
{
    private static readonly Guid _server = Guid.NewGuid();
    private static readonly string _root = Path.Combine(Path.GetTempPath(), "federata");

    private readonly Mock<ILibraryManager> _library = new();
    private readonly List<VirtualFolderInfo> _folders = [];
    private readonly List<(string Library, string Path)> _added = [];
    private readonly List<(string Library, string Path)> _removed = [];
    private bool _silent;

    public LibraryPathsTests()
    {
        _library.Setup(library => library.GetVirtualFolders()).Returns(() => _folders.Select(Copy).ToList());
        _library.Setup(library => library.AddMediaPath(It.IsAny<string>(), It.IsAny<MediaPathInfo>()))
            .Callback((string name, MediaPathInfo path) =>
            {
                _added.Add((name, path.Path));
                if (!_silent)
                {
                    var folder = _folders.Single(folder => folder.Name == name);
                    folder.Locations = [.. folder.Locations, path.Path];
                }
            });
        _library.Setup(library => library.RemoveMediaPath(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string name, string path) =>
            {
                _removed.Add((name, path));
                var folder = _folders.Single(folder => folder.Name == name);

                // Jellyfin drops only a path given exactly as stored.
                if (!_silent)
                {
                    folder.Locations = folder.Locations.Where(location => !string.Equals(location, path, StringComparison.Ordinal)).ToArray();
                }
            });
    }

    private static string ServerMovies => FederatedFolderLayout.ServerMoviesPath(_root, _server);

    private static string OldMovies => FederatedFolderLayout.MoviesPath(_root);

    [Fact]
    public void AddsAServerFolderTheLibraryLacks()
    {
        _folders.Add(Folder("Film", "/media/film", OldMovies));

        var outcome = Paths().Add(_root, "Film", ServerMovies);

        Assert.Equal(LibraryPathOutcome.Done, outcome);
        Assert.Equal([("Film", ServerMovies)], _added);
        Assert.Equal(["/media/film", OldMovies, ServerMovies], _folders[0].Locations);
    }

    [Fact]
    public void ANewFolderAlreadyThereIsNotAddedAgain()
    {
        // A second add would write a second shortcut that one removal cannot undo (R:prova-librerie flag 4).
        _folders.Add(Folder("Film", OldMovies, ServerMovies + Path.DirectorySeparatorChar));

        var outcome = Paths().Add(_root, "Film", ServerMovies);

        Assert.Equal(LibraryPathOutcome.Unchanged, outcome);
        Assert.Empty(_added);
    }

    [Fact]
    public void AnAddThatLeavesNoTraceFails()
    {
        _folders.Add(Folder("Film", OldMovies));
        _silent = true;

        Assert.Equal(LibraryPathOutcome.Failed, Paths().Add(_root, "Film", ServerMovies));
    }

    [Fact]
    public void AnAddThatThrowsFails()
    {
        _folders.Add(Folder("Film", OldMovies));
        _library.Setup(library => library.AddMediaPath(It.IsAny<string>(), It.IsAny<MediaPathInfo>())).Throws(new FileNotFoundException("The path does not exist."));

        Assert.Equal(LibraryPathOutcome.Failed, Paths().Add(_root, "Film", ServerMovies));
    }

    [Fact]
    public void AMissingLibraryIsNotTouched()
    {
        _folders.Add(Folder("Film", OldMovies));

        Assert.Equal(LibraryPathOutcome.NoLibrary, Paths().Add(_root, "Serie", FederatedFolderLayout.ServerSeriesPath(_root, _server)));
        Assert.Equal(LibraryPathOutcome.NoLibrary, Paths().Remove(_root, "Serie", FederatedFolderLayout.SeriesPath(_root)));
        Assert.Empty(_added);
        Assert.Empty(_removed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Film/Dune (2021)")]
    [InlineData("other/Film")]
    [InlineData("../media/film")]
    [InlineData("not-a-guid/Film")]
    public void OnlyTheFolderPathsOfTheLayoutAreTaken(string relative)
    {
        // The plugin changes the libraries only by the folders of the federated folder (§10 row 77).
        var path = relative.Length == 0 ? _root : Path.GetFullPath(Path.Combine(_root, relative));
        _folders.Add(Folder("Film", OldMovies, path));

        Assert.Equal(LibraryPathOutcome.Refused, Paths().Add(_root, "Film", path));
        Assert.Equal(LibraryPathOutcome.Refused, Paths().Remove(_root, "Film", path));
        Assert.Empty(_added);
        Assert.Empty(_removed);
    }

    [Fact]
    public void ARemovalUsesThePathAsTheLibraryStoresIt()
    {
        var stored = OldMovies + Path.DirectorySeparatorChar;
        _folders.Add(Folder("Film", "/media/film", stored, ServerMovies));

        var outcome = Paths().Remove(_root, "Film", OldMovies);

        Assert.Equal(LibraryPathOutcome.Done, outcome);
        Assert.Equal([("Film", stored)], _removed);
        Assert.Equal(["/media/film", ServerMovies], _folders[0].Locations);
    }

    [Fact]
    public void AFolderTheLibraryDoesNotHoldIsNotRemoved()
    {
        _folders.Add(Folder("Film", "/media/film", ServerMovies));

        Assert.Equal(LibraryPathOutcome.Unchanged, Paths().Remove(_root, "Film", OldMovies));
        Assert.Empty(_removed);
    }

    [Fact]
    public void ARemovalThatLeavesThePathFails()
    {
        _folders.Add(Folder("Film", OldMovies, ServerMovies));
        _silent = true;

        Assert.Equal(LibraryPathOutcome.Failed, Paths().Remove(_root, "Film", OldMovies));
    }

    [Fact]
    public void UnreadableLibrariesFail()
    {
        _library.Setup(library => library.GetVirtualFolders()).Throws<IOException>();

        Assert.Equal(LibraryPathOutcome.Failed, Paths().Add(_root, "Film", ServerMovies));
        Assert.Equal(LibraryPathOutcome.Failed, Paths().Remove(_root, "Film", OldMovies));
    }

    private static VirtualFolderInfo Folder(string name, params string[] locations) => new() { Name = name, Locations = locations };

    private static VirtualFolderInfo Copy(VirtualFolderInfo folder) => new() { Name = folder.Name, Locations = folder.Locations.ToArray(), ItemId = folder.ItemId };

    private LibraryPaths Paths() => new(_library.Object, new CapturingLogger<LibraryPaths>());
}
