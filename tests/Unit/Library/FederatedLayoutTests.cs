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
    public void FilmsSeriesMusicMusicVideosAndHomeVideosAreWritten()
    {
        Assert.Equal([TypeFolder.Movies, TypeFolder.Series, TypeFolder.Music, TypeFolder.MusicVideos, TypeFolder.HomeVideos], TypeFolder.Written);
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
        Assert.Equal(TypeFolder.Music, TypeFolder.Of(StubKind.Track));
        Assert.Equal(TypeFolder.MusicVideos, TypeFolder.Of(StubKind.MusicVideo));
        Assert.Equal(TypeFolder.HomeVideos, TypeFolder.Of(StubKind.HomeVideo));
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
    [InlineData("{01234567-89ab-cdef-0123-456789abcdef}")]
    [InlineData("0123456789abcdef0123456789abcdef ")]
    [InlineData(" 0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef\n")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public void ARemoteLibraryIdThatIsNotAPlainIdNeverBecomesAFolder(string libraryId)
    {
        Assert.False(FederatedPaths.IsLibraryId(libraryId));
        Assert.Throws<ArgumentException>(() => FederatedPaths.LibraryFolder(_server, libraryId));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("f11e0000000000000000000000000001")]
    public void AJellyfinLibraryIdIsAFolderName(string libraryId)
    {
        Assert.True(FederatedPaths.IsLibraryId(libraryId));
        Assert.Equal(Path.Combine(_server.ToString("D"), libraryId), FederatedPaths.LibraryFolder(_server, libraryId));
    }

    [Fact]
    public void TheRemoteLibraryOfAStubIsReadFromItsPath()
    {
        var films = FederatedPaths.TypeFolderOf(_server, LibraryId, TypeFolder.Movies);
        var shows = FederatedPaths.TypeFolderOf(_server, LibraryId, TypeFolder.Series);

        Assert.Equal(LibraryId, FederatedPaths.RemoteLibraryOf(FederatedPaths.MovieStubIn(films, "Film (2020)", "Casa")));
        Assert.Equal(LibraryId, FederatedPaths.RemoteLibraryOf(Path.Combine(shows, "Serie (2020)")));
        Assert.Equal(LibraryId, FederatedPaths.RemoteLibraryOf(FederatedPaths.EpisodeStub(Path.Combine(shows, "Serie (2020)"), "Serie", 1, 2)));
        Assert.Equal(LibraryId, FederatedPaths.RemoteLibraryOf(Path.Combine(FederatedPaths.TypeFolderOf(_server, LibraryId, TypeFolder.Books), "Libro")));
    }

    [Fact]
    public void AStubOfTheOlderLayoutsHasNoRemoteLibrary()
    {
        Assert.Null(FederatedPaths.RemoteLibraryOf(FederatedPaths.MovieStub("Film (2020)")));
        Assert.Null(FederatedPaths.RemoteLibraryOf(FederatedPaths.MovieStub(_server, "Film (2020)", "Casa")));
        Assert.Null(FederatedPaths.RemoteLibraryOf(Path.Combine(FederatedPaths.SeriesFolderOf(_server, "Serie (2020)"), "Season 01")));
        Assert.Null(FederatedPaths.RemoteLibraryOf(FederatedPaths.MovieStub(_server, LibraryId, "Casa")));
        Assert.Null(FederatedPaths.RemoteLibraryOf(Path.Combine("not-a-server", LibraryId, "Film", "Film")));
        Assert.Null(FederatedPaths.RemoteLibraryOf(Path.Combine(_server.ToString("D"), LibraryId, "Altro", "Film")));
        Assert.Null(FederatedPaths.RemoteLibraryOf(Path.Combine(_server.ToString("N"), LibraryId, "Film", "Film")));
    }
}
