using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The titles of a remote library that vanished leave the federated folder; the other libraries keep theirs (§10 row 436).
public sealed class VanishedLibraryStubTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<RemoteServerInfo> TwoLibrariesMaterializedAsync()
    {
        _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsB, "Film B", "movies"));
        _fixture.Sync.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        _fixture.Sync.Client.Add(TestLibraries.FilmsB, RemoteItemType.Movie, TestItems.Movie("b", "Altro", 2021, "2"));
        var server = await _fixture.Sync.AddServerAsync("A", TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        return server;
    }

    private bool HasFilesIn(RemoteServerInfo server, string libraryId)
        => _fixture.Files.Any(file => file.StartsWith(FederatedPaths.LibraryFolder(server.Id, libraryId), StringComparison.Ordinal) && file.EndsWith(".strm", StringComparison.Ordinal));

    [Fact]
    public async Task One_absence_keeps_the_titles_of_the_library()
    {
        var server = await TwoLibrariesMaterializedAsync();
        _fixture.Sync.Client.Libraries.RemoveAll(library => library.Id == TestLibraries.FilmsB);

        await _fixture.SyncAndMaterializeAsync();

        Assert.True(HasFilesIn(server, TestLibraries.FilmsB));
        Assert.True(HasFilesIn(server, TestLibraries.Films));
    }

    [Fact]
    public async Task The_second_absence_in_a_row_removes_the_titles_of_that_library_only()
    {
        var server = await TwoLibrariesMaterializedAsync();
        Assert.True(HasFilesIn(server, TestLibraries.FilmsB));
        _fixture.Sync.Client.Libraries.RemoveAll(library => library.Id == TestLibraries.FilmsB);

        await _fixture.SyncAndMaterializeAsync();
        await _fixture.SyncAndMaterializeAsync();

        Assert.False(HasFilesIn(server, TestLibraries.FilmsB));
        Assert.True(HasFilesIn(server, TestLibraries.Films));
    }

    [Fact]
    public async Task A_remote_error_on_the_library_list_removes_nothing()
    {
        var server = await TwoLibrariesMaterializedAsync();
        _fixture.Sync.Client.Libraries.RemoveAll(library => library.Id == TestLibraries.FilmsB);
        await _fixture.SyncAndMaterializeAsync();
        _fixture.Sync.Client.LibrariesFailure = RemoteFailure.ServerError;

        await _fixture.SyncAndMaterializeAsync();
        await _fixture.SyncAndMaterializeAsync();

        Assert.True(HasFilesIn(server, TestLibraries.FilmsB));
        Assert.True(HasFilesIn(server, TestLibraries.Films));
    }
}
