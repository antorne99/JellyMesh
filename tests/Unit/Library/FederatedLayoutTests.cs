using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class FederatedLayoutTests
{
    private const string LibraryId = "0123456789abcdef0123456789abcdef";

    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");

    [Fact]
    public void TheSixTypeFoldersMatchTheLocalLibraryTypes()
    {
        Assert.Equal(
            [
                ("Film", CollectionTypeOptions.movies),
                ("Serie", CollectionTypeOptions.tvshows),
                ("Musica", CollectionTypeOptions.music),
                ("Video musicali", CollectionTypeOptions.musicvideos),
                ("Video domestici", CollectionTypeOptions.homevideos),
                ("Libri", CollectionTypeOptions.books)
            ],
            TypeFolder.All.Select(type => (type.Name, type.LibraryType)));
    }

    [Fact]
    public void OnlyFilmsAndSeriesAreWrittenUntilTheNewTypesArrive()
    {
        Assert.Equal([TypeFolder.Movies, TypeFolder.Series], TypeFolder.Written);
    }

    [Fact]
    public void EveryTypeFolderSitsUnderTheServerAndTheRemoteLibraryId()
    {
        var root = Path.Combine(Path.GetTempPath(), "federated");

        foreach (var type in TypeFolder.All)
        {
            Assert.Equal(
                Path.Combine(root, _server.ToString("D"), LibraryId, type.Name),
                FederatedFolderLayout.TypeFolderPath(root, _server, LibraryId, type));
        }
    }

    [Fact]
    public void AStubKindAndAContentKindNameTheirTypeFolder()
    {
        Assert.Equal(TypeFolder.Movies, TypeFolder.Of(StubKind.Movie));
        Assert.Equal(TypeFolder.Series, TypeFolder.Of(StubKind.Episode));
        Assert.Equal(TypeFolder.Movies, TypeFolder.Of(ContentKind.Movie));
        Assert.Equal(TypeFolder.Series, TypeFolder.Of(ContentKind.Episode));
    }

    [Fact]
    public void AFilmStubOfARemoteLibraryCarriesTheLabelOfItsServer()
    {
        var folder = FederatedPaths.TypeFolderOf(_server, LibraryId, TypeFolder.Movies);

        Assert.Equal(
            Path.Combine(_server.ToString("D"), LibraryId, "Film", "Film (2020)", "Film (2020) - Casa.strm"),
            FederatedPaths.MovieStubIn(folder, "Film (2020)", "Casa"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("Film")]
    [InlineData("Serie")]
    [InlineData("CON")]
    [InlineData("a/b")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
    public void ARemoteLibraryIdThatIsNotAPlainIdNeverBecomesAFolder(string libraryId)
    {
        Assert.False(FederatedPaths.IsLibraryId(libraryId));
        Assert.Throws<ArgumentException>(() => FederatedPaths.LibraryFolder(_server, libraryId));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public void AJellyfinLibraryIdIsAFolderName(string libraryId)
    {
        Assert.True(FederatedPaths.IsLibraryId(libraryId));
        Assert.Equal(Path.Combine(_server.ToString("D"), libraryId), FederatedPaths.LibraryFolder(_server, libraryId));
    }
}
