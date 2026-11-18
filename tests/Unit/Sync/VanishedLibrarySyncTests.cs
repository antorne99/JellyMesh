using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Activity;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// A remote library absent from two consecutive correct answers leaves the imported libraries (§10 row 436).
public sealed class VanishedLibrarySyncTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private ServerCatalog CatalogOf(RemoteServerInfo server) => _fixture.Index.Snapshot.Find(server.Id)!;

    private RemoteServerConfig ConfigOf(RemoteServerInfo server) => _fixture.Store.Configuration.Servers.Single(entry => entry.Id == server.Id);

    private async Task<RemoteServerInfo> TwoLibrariesSyncedAsync()
    {
        _fixture.Client.Add(TestLibraries.Films, RemoteItemType.Movie, TestItems.Movie("m"));
        _fixture.Client.Add(TestLibraries.FilmsB, RemoteItemType.Movie, TestItems.Movie("b", "Other", 2021, "2"));
        _fixture.Client.Libraries.Add(new RemoteLibrary(TestLibraries.FilmsB, "Film B", "movies"));
        var server = await _fixture.AddServerAsync("A", TestLibraries.Films, TestLibraries.FilmsB);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        return server;
    }

    private void RemoveOnRemote(string libraryId) => _fixture.Client.Libraries.RemoveAll(library => library.Id == libraryId);

    [Fact]
    public async Task A_library_absent_from_one_correct_answer_stays_imported_and_keeps_its_entries()
    {
        var server = await TwoLibrariesSyncedAsync();
        RemoveOnRemote(TestLibraries.FilmsB);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Contains(TestLibraries.FilmsB, ConfigOf(server).ImportedLibraryIds);
        Assert.Equal(["b", "m"], CatalogOf(server).Items.Select(item => item.ItemId).Order());
        Assert.Equal(RemoteFailure.LibraryUnavailable, CatalogOf(server).LastFailure);
    }

    [Fact]
    public async Task A_library_absent_from_two_consecutive_correct_answers_leaves_the_imported_libraries_and_the_index()
    {
        var server = await TwoLibrariesSyncedAsync();
        RemoveOnRemote(TestLibraries.FilmsB);

        await _fixture.Sync.RunAsync(CancellationToken.None);
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal([TestLibraries.Films], ConfigOf(server).ImportedLibraryIds);
        Assert.Equal(["m"], CatalogOf(server).Items.Select(item => item.ItemId));
        Assert.Equal(ServerSyncState.Complete, CatalogOf(server).State);
        Assert.DoesNotContain(ConfigOf(server).RemoteLibraries, library => library.Id == TestLibraries.FilmsB);
    }

    [Fact]
    public async Task A_vanished_library_is_recorded_with_its_name_logged_and_written_to_the_activity_log()
    {
        var server = await TwoLibrariesSyncedAsync();
        RemoveOnRemote(TestLibraries.FilmsB);

        await _fixture.Sync.RunAsync(CancellationToken.None);
        Assert.Empty(_fixture.Activity.OfType(ActivityEntries.LibraryVanishedType));
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal([(TestLibraries.FilmsB, "Film B")], ConfigOf(server).VanishedLibraries.Select(library => (library.Id, library.Name)));
        Assert.Empty(ConfigOf(server).AbsentLibraryIds);
        var entry = Assert.Single(_fixture.Activity.OfType(ActivityEntries.LibraryVanishedType));
        Assert.Contains("Film B", entry.Name, StringComparison.Ordinal);
        Assert.Contains(_fixture.ReaderLogger.Lines, line => line.Contains("Film B", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_library_listed_again_after_one_absence_starts_counting_from_zero()
    {
        var server = await TwoLibrariesSyncedAsync();
        var library = _fixture.Client.Libraries.Single(library => library.Id == TestLibraries.FilmsB);
        RemoveOnRemote(TestLibraries.FilmsB);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Libraries.Add(library);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        RemoveOnRemote(TestLibraries.FilmsB);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Contains(TestLibraries.FilmsB, ConfigOf(server).ImportedLibraryIds);
        Assert.Equal([TestLibraries.FilmsB], ConfigOf(server).AbsentLibraryIds);
        Assert.Empty(ConfigOf(server).VanishedLibraries);
    }

    [Fact]
    public async Task A_failed_library_list_neither_counts_nor_resets_the_absence()
    {
        var server = await TwoLibrariesSyncedAsync();
        RemoveOnRemote(TestLibraries.FilmsB);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.LibrariesFailure = RemoteFailure.ServerError;
        await _fixture.Sync.RunAsync(CancellationToken.None);
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Contains(TestLibraries.FilmsB, ConfigOf(server).ImportedLibraryIds);
        Assert.Equal([TestLibraries.FilmsB], ConfigOf(server).AbsentLibraryIds);
        Assert.Equal(["b", "m"], CatalogOf(server).Items.Select(item => item.ItemId).Order());

        _fixture.Client.LibrariesFailure = RemoteFailure.None;
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal([TestLibraries.Films], ConfigOf(server).ImportedLibraryIds);
    }

    [Fact]
    public async Task An_empty_library_list_does_not_count_as_an_absence()
    {
        var server = await TwoLibrariesSyncedAsync();
        _fixture.Client.Libraries.Clear();

        await _fixture.Sync.RunAsync(CancellationToken.None);
        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal([TestLibraries.Films, TestLibraries.FilmsB], ConfigOf(server).ImportedLibraryIds);
        Assert.Empty(ConfigOf(server).AbsentLibraryIds);
        Assert.Equal(2, CatalogOf(server).Items.Count);
    }

    [Fact]
    public async Task A_page_that_fails_after_a_second_absence_keeps_the_entries_of_the_other_libraries()
    {
        var server = await TwoLibrariesSyncedAsync();
        RemoveOnRemote(TestLibraries.FilmsB);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.OnPage = (_, _) => RemoteFailure.ServerError;

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal([TestLibraries.Films], ConfigOf(server).ImportedLibraryIds);
        Assert.Contains("m", CatalogOf(server).Items.Select(item => item.ItemId));
    }

    [Fact]
    public async Task Importing_a_vanished_library_again_clears_its_record()
    {
        var server = await TwoLibrariesSyncedAsync();
        var library = _fixture.Client.Libraries.Single(library => library.Id == TestLibraries.FilmsB);
        RemoveOnRemote(TestLibraries.FilmsB);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Libraries.Add(library);
        _fixture.Registry.SetImportedLibraries(server.Id, [TestLibraries.Films, TestLibraries.FilmsB]);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Empty(ConfigOf(server).VanishedLibraries);
        Assert.Equal(2, CatalogOf(server).Items.Count);
    }
}
