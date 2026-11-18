using System;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class StubLayoutTests
{
    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");

    private static StubEntry Stub(string path) => new()
    {
        Id = path,
        Kind = StubKind.Movie,
        ServerId = _server,
        RelativePath = path,
        IdentityKey = path,
        Sources = []
    };

    private static SeriesStub Series(string path) => new() { Id = path, ServerId = _server, RelativePath = path, WorkKey = path };

    [Fact]
    public void AnEmptyRegistryUsesTheRemoteLibraryLayout() => Assert.False(StubLayout.IsLegacy(StubState.Empty));

    [Fact]
    public void AStubOfTheRemoteLibraryLayoutKeepsIt()
        => Assert.False(StubLayout.IsLegacy(StubState.Empty with { Stubs = [Stub(FederatedPaths.MovieStubIn(FederatedPaths.TypeFolderOf(_server, TestLibraries.Films, TypeFolder.Movies), "Film (2020)", "A"))] }));

    [Fact]
    public void AStubOfTheServerFoldersKeepsTheOlderLayout()
        => Assert.True(StubLayout.IsLegacy(StubState.Empty with { Stubs = [Stub(FederatedPaths.MovieStub(_server, "Film (2020)", "A"))] }));

    [Fact]
    public void AFlatStubKeepsTheOlderLayout()
        => Assert.True(StubLayout.IsLegacy(StubState.Empty with { Stubs = [Stub(Path.Combine("Film", "Film (2020)", "Film (2020).strm"))] }));

    [Fact]
    public void ASeriesOfTheServerFoldersKeepsTheOlderLayout()
        => Assert.True(StubLayout.IsLegacy(StubState.Empty with { Series = [Series(Path.Combine(FederatedPaths.ServerFolder(_server), "Serie", "Show (2020)"))] }));

    [Fact]
    public void TheTypeFoldersOfAServerAreThoseHoldingItsStubs()
    {
        var films = FederatedPaths.TypeFolderOf(_server, TestLibraries.Films, TypeFolder.Movies);
        var filmsB = FederatedPaths.TypeFolderOf(_server, TestLibraries.FilmsB, TypeFolder.Movies);
        var shows = FederatedPaths.TypeFolderOf(_server, TestLibraries.Shows, TypeFolder.Series);
        var state = StubState.Empty with
        {
            Stubs =
            [
                Stub(FederatedPaths.MovieStubIn(filmsB, "Film (2020)", "A")),
                Stub(FederatedPaths.MovieStubIn(films, "Film (2020)", "A")),
                Stub(FederatedPaths.MovieStubIn(films, "Other (2021)", "A")),
                Stub(Path.Combine(shows, "Show (2020)", "Season 01", "Show S01E01.strm"))
            ],
            Series = [Series(Path.Combine(shows, "Show (2020)"))]
        };

        Assert.Equal([films, filmsB], StubLayout.TypeFoldersOf(state, _server, TypeFolder.Movies));
        Assert.Equal([shows], StubLayout.TypeFoldersOf(state, _server, TypeFolder.Series));
        Assert.Empty(StubLayout.TypeFoldersOf(state, Guid.NewGuid(), TypeFolder.Movies));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFolderWithoutOlderStubFilesHasNone(bool anchors)
    {
        var root = Path.Combine(Path.GetTempPath(), "jellymesh-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (anchors)
            {
                Write(root, Path.Combine(FederatedPaths.ServerFolder(_server), "Film", ".jellymesh"));
                Write(root, Path.Combine("Serie", ".jellymesh"));
                Write(root, FederatedPaths.MovieStubIn(FederatedPaths.TypeFolderOf(_server, TestLibraries.Films, TypeFolder.Movies), "Film (2020)", "A"));
            }

            Assert.False(StubLayout.HasOlderStubFiles(root));
        }
        finally
        {
            Delete(root);
        }
    }

    [Theory]
    [InlineData("server")]
    [InlineData("flat")]
    [InlineData("episode")]
    public void AStubFileOfAnOlderLayoutIsFound(string where)
    {
        var root = Path.Combine(Path.GetTempPath(), "jellymesh-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            Write(root, where switch
            {
                "server" => FederatedPaths.MovieStub(_server, "Film (2020)", "A"),
                "flat" => Path.Combine("Film", "Film (2020)", "Film (2020).strm"),
                _ => Path.Combine(FederatedPaths.ServerFolder(_server), "Serie", "Show (2020)", "Season 01", "Show S01E01.strm")
            });

            Assert.True(StubLayout.HasOlderStubFiles(root));
        }
        finally
        {
            Delete(root);
        }
    }

    private static void Write(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
