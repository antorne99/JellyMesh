using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

/// <summary>Which type folders of a server go to the same local libraries (§10 rows 439 and 507).</summary>
public class LibraryPlacementTests
{
    private const string FilmsA = TestLibraries.Films;
    private const string FilmsB = TestLibraries.FilmsB;
    private const string FilmsC = TestLibraries.FilmsC;

    private static readonly string _root = Path.Combine(Path.GetTempPath(), "federata");
    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");

    private static string Folder(string library, TypeFolder? type = null, Guid? server = null) =>
        FederatedFolderLayout.TypeFolderPath(_root, server ?? _server, library, type ?? TypeFolder.Movies);

    private static VirtualFolderInfo Local(string name, params string[] locations) => new() { Name = name, Locations = locations };

    private static LibraryPlacement Placement(LibraryMode mode, params VirtualFolderInfo[] folders) => LibraryPlacement.From(folders, _root, _ => mode);

    private static bool AreTogether(LibraryPlacement placement, string left, string right) =>
        placement.KeyOf(_server, left, TypeFolder.Movies) == placement.KeyOf(_server, right, TypeFolder.Movies);

    [Theory]
    [InlineData(LibraryMode.ByType)]
    [InlineData(LibraryMode.ByRemoteLibrary)]
    [InlineData(LibraryMode.None)]
    public void FoldersInTheSameLocalLibraryAreTogetherInEveryMode(LibraryMode mode)
        => Assert.True(AreTogether(Placement(mode, Local("Film", Folder(FilmsA), Folder(FilmsB))), FilmsA, FilmsB));

    [Theory]
    [InlineData(LibraryMode.ByType)]
    [InlineData(LibraryMode.ByRemoteLibrary)]
    [InlineData(LibraryMode.None)]
    public void FoldersInDifferentLocalLibrariesAreApartInEveryMode(LibraryMode mode)
        => Assert.False(AreTogether(Placement(mode, Local("Film", Folder(FilmsA)), Local("Film B", Folder(FilmsB))), FilmsA, FilmsB));

    [Fact]
    public void TheSingleLibraryCountsLikeAnyOther()
    {
        var single = Local("Tutti", Folder(FilmsA), Folder(FilmsB), Folder(FilmsC));
        var placement = Placement(LibraryMode.ByRemoteLibrary, Local("A", Folder(FilmsA)), Local("B", Folder(FilmsB)), single);

        Assert.False(AreTogether(placement, FilmsA, FilmsB));
        Assert.False(AreTogether(placement, FilmsA, FilmsC));
    }

    [Fact]
    public void TheOrderOfTheLibrariesOfAFolderDoesNotCount()
    {
        var placement = Placement(LibraryMode.None, Local("Tutti", Folder(FilmsB), Folder(FilmsA)), Local("Film", Folder(FilmsA), Folder(FilmsB)));

        Assert.True(AreTogether(placement, FilmsA, FilmsB));
    }

    [Theory]
    [InlineData(LibraryMode.ByType, true)]
    [InlineData(LibraryMode.None, true)]
    [InlineData(LibraryMode.ByRemoteLibrary, false)]
    public void FoldersInNoLocalLibraryFollowTheMode(LibraryMode mode, bool together)
        => Assert.Equal(together, AreTogether(Placement(mode), FilmsA, FilmsB));

    [Theory]
    [InlineData(LibraryMode.ByType, true)]
    [InlineData(LibraryMode.None, true)]
    [InlineData(LibraryMode.ByRemoteLibrary, false)]
    public void AFolderInNoLocalLibraryJoinsTheOthersOfItsServerAndTypeWhenTheyAreTogether(LibraryMode mode, bool together)
        => Assert.Equal(together, AreTogether(Placement(mode, Local("Film", Folder(FilmsA), Folder(FilmsC))), FilmsA, FilmsB));

    [Fact]
    public void AFolderInNoLocalLibraryIsApartWhenTheOthersOfItsServerAndTypeAreApart()
    {
        var placement = Placement(LibraryMode.ByType, Local("A", Folder(FilmsA)), Local("C", Folder(FilmsC)));

        Assert.False(AreTogether(placement, FilmsA, FilmsB));
        Assert.False(AreTogether(placement, FilmsC, FilmsB));
    }

    [Fact]
    public void AFolderInNoLocalLibraryIgnoresTheFoldersOfOtherServersAndTypes()
    {
        var placement = Placement(LibraryMode.ByType, Local("Altri", Folder(FilmsA, server: _other)), Local("Serie", Folder(FilmsA, TypeFolder.Series)));

        Assert.Equal(placement.KeyOf(_server, FilmsA, TypeFolder.Movies), placement.KeyOf(_server, FilmsB, TypeFolder.Movies));
    }

    [Fact]
    public void TheModeOfEachServerDecidesForItsOwnFolders()
    {
        var placement = LibraryPlacement.From([], _root, server => server == _server ? LibraryMode.ByRemoteLibrary : LibraryMode.ByType);

        Assert.NotEqual(placement.KeyOf(_server, FilmsA, TypeFolder.Movies), placement.KeyOf(_server, FilmsB, TypeFolder.Movies));
        Assert.Equal(placement.KeyOf(_other, FilmsA, TypeFolder.Movies), placement.KeyOf(_other, FilmsB, TypeFolder.Movies));
    }

    [Fact]
    public void ALibraryOnAParentFolderDoesNotHoldTheTypeFolder()
    {
        var placement = Placement(LibraryMode.ByRemoteLibrary, Local("Tutto", _root), Local("Server", FederatedFolderLayout.ServerPath(_root, _server)));

        Assert.False(AreTogether(placement, FilmsA, FilmsB));
    }

    [Fact]
    public void TrailingSeparatorsMakeNoOtherFolderAndUnreadableLocationsAreIgnored()
    {
        var placement = Placement(
            LibraryMode.ByRemoteLibrary,
            Local("Film", Folder(FilmsA) + Path.DirectorySeparatorChar, Folder(FilmsB), "bad\0path", string.Empty),
            new VirtualFolderInfo { Name = "Vuota", Locations = null! });

        Assert.True(AreTogether(placement, FilmsA, FilmsB));
    }

    [Fact]
    public void FilmsAndSeriesOfOneRemoteLibraryAreNeverTogether()
    {
        var placement = Placement(LibraryMode.ByType, Local("Mista", Folder(FilmsA), Folder(FilmsA, TypeFolder.Series)));
        var unplaced = Placement(LibraryMode.ByRemoteLibrary);

        Assert.All(new[] { placement, unplaced }, entry =>
            Assert.NotEqual(entry.KeyOf(_server, FilmsA, TypeFolder.Movies), entry.KeyOf(_server, FilmsA, TypeFolder.Series)));
    }
}
