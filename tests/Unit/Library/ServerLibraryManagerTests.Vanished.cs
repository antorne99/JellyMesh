using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The library of a remote library that vanished goes once its titles are gone; an adopted one only loses its paths (§10 row 436).
public sealed partial class ServerLibraryManagerTests
{
    // Like the reader after a second absence: the remote library leaves the imported ones and is recorded as vanished.
    private static void Vanish(Fixture fixture, string libraryId, string name)
    {
        fixture.Server.ImportedLibraryIds.Remove(libraryId);
        fixture.Server.RemoteLibraries.Remove(fixture.Server.RemoteLibraries.Single(library => library.Id == libraryId));
        fixture.Server.VanishedLibraries.Add(new VanishedLibraryConfig { Id = libraryId, Name = name });
    }

    private static void HoldsSeries(Fixture fixture) => fixture.Stubs.Setup(stubs => stubs.TypeFoldersOf(_serverId, TypeFolder.Series)).Returns([_showsFolder]);

    private static async Task<Fixture> ByRemoteLibraryWithFilmsB()
    {
        var fixture = OnTypeFolders();
        HoldsFilms(fixture, _filmsFolder, _filmsBFolder);
        HoldsSeries(fixture);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        return fixture;
    }

    [Fact]
    public async Task The_library_of_a_vanished_remote_library_stays_while_its_titles_are_there()
    {
        var fixture = await ByRemoteLibraryWithFilmsB();
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record.Name == "Casa - Film 4K");
    }

    [Fact]
    public async Task While_the_registry_cannot_tell_where_the_titles_are_the_library_of_a_vanished_remote_library_stays()
    {
        var fixture = await ByRemoteLibraryWithFilmsB();
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        fixture.Stubs.Setup(stubs => stubs.TypeFoldersOf(_serverId, TypeFolder.Movies)).Returns((string[]?)null);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Libraries.RemovedPaths);
    }

    [Fact]
    public async Task The_own_library_of_a_vanished_remote_library_is_deleted_once_its_titles_are_gone_and_can_be_made_again()
    {
        var fixture = await ByRemoteLibraryWithFilmsB();
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        HoldsFilms(fixture, _filmsFolder);
        fixture.Scans.Steps.Clear();

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film 4K"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.DoesNotContain(fixture.Server.AutomaticLibraryRegistry, record => record.RemoteLibraryId == TestLibraries.FilmsB);
        Assert.DoesNotContain(fixture.Server.CreatedLibraryKeys, key => key.Contains(TestLibraries.FilmsB, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Folders.Select(folder => folder.Name).Order());
        Assert.Contains("scan", fixture.Scans.Steps);
    }

    // Runs a check that stops at its first wait for the scans, changes the libraries there, then lets it go on.
    private static async Task ReconcileChangingDuringTheWaitAsync(Fixture fixture, Action change)
    {
        var release = new TaskCompletionSource();
        fixture.Scans.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var check = fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.Scans.Entered.Task;
        change();
        fixture.Scans.Block = Task.CompletedTask;
        release.SetResult();
        await check;
    }

    [Fact]
    public async Task An_own_library_renamed_while_waiting_for_the_scans_is_left_until_the_next_check_deletes_it_under_its_new_name()
    {
        var fixture = await ByRemoteLibraryWithFilmsB();
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        HoldsFilms(fixture, _filmsFolder);

        await ReconcileChangingDuringTheWaitAsync(fixture, () => fixture.Libraries.RenameFromDashboard("Casa - Film 4K", "Film in 4K"));
        Assert.Empty(fixture.Libraries.Removed);
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Film in 4K"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task An_own_library_given_a_folder_of_the_admin_while_waiting_for_the_scans_is_left_alone()
    {
        var fixture = await ByRemoteLibraryWithFilmsB();
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        HoldsFilms(fixture, _filmsFolder);

        await ReconcileChangingDuringTheWaitAsync(fixture, () =>
        {
            var folder = fixture.Libraries.Folders.Single(folder => folder.Name == "Casa - Film 4K");
            folder.Locations = [.. folder.Locations, "/media/miei"];
        });

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Contains(fixture.Libraries.Folders, folder => folder.Name == "Casa - Film 4K");
    }

    [Fact]
    public async Task A_by_type_library_only_loses_the_folder_of_the_vanished_remote_library()
    {
        var fixture = OnTypeFolders();
        HoldsFilms(fixture, _filmsFolder, _filmsBFolder);
        HoldsSeries(fixture);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        HoldsFilms(fixture, _filmsFolder);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal([("Casa - Film", Absolute(_filmsBFolder))], fixture.Libraries.RemovedPaths);
        Assert.Equal([Absolute(_filmsFolder)], fixture.Libraries.Find("Casa - Film").Locations);
    }

    [Fact]
    public async Task A_by_type_library_that_held_only_the_vanished_remote_library_is_deleted()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Films);
        HoldsFilms(fixture, _filmsBFolder);
        HoldsSeries(fixture);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        Assert.Equal([Absolute(_filmsBFolder)], fixture.Libraries.Find("Casa - Film").Locations);
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        HoldsFilms(fixture);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
    }

    [Fact]
    public async Task An_adopted_library_only_loses_the_folder_of_the_vanished_remote_library_and_keeps_its_name()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film 4K di casa", CollectionTypeOptions.movies, Absolute(_filmsBFolder));
        HoldsFilms(fixture, _filmsFolder, _filmsBFolder);
        HoldsSeries(fixture);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record is { Name: "Film 4K di casa", Adopted: true });
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        HoldsFilms(fixture, _filmsFolder);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal([("Film 4K di casa", Absolute(_filmsBFolder))], fixture.Libraries.RemovedPaths);
        Assert.Contains(fixture.Libraries.Folders, folder => folder.Name == "Film 4K di casa");
        Assert.Empty(fixture.Renamer.Calls);
    }

    [Fact]
    public async Task A_library_of_the_admin_that_holds_the_folder_of_a_vanished_remote_library_is_left_alone()
    {
        var fixture = await ByRemoteLibraryWithFilmsB();
        var other = Absolute(FederatedPaths.TypeFolderOf(Guid.NewGuid(), TestLibraries.Films, TypeFolder.Movies));
        fixture.Libraries.Add("Tutti i film", CollectionTypeOptions.movies, Absolute(_filmsBFolder), other);
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        HoldsFilms(fixture, _filmsFolder);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([Absolute(_filmsBFolder), other], fixture.Libraries.Find("Tutti i film").Locations);
        Assert.DoesNotContain(fixture.Libraries.RemovedPaths, removed => removed.Name == "Tutti i film");
    }

    [Fact]
    public async Task While_a_library_has_no_id_yet_nothing_of_a_vanished_remote_library_is_removed()
    {
        var fixture = await ByRemoteLibraryWithFilmsB();
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        HoldsFilms(fixture, _filmsFolder);
        fixture.Libraries.Folders.Add(new VirtualFolderInfo { Name = "Nuova", CollectionType = CollectionTypeOptions.movies, Locations = ["/media/nuova"] });

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Libraries.RemovedPaths);
    }

    [Fact]
    public async Task A_check_after_the_cleanup_changes_nothing_more()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film 4K di casa", CollectionTypeOptions.movies, Absolute(_filmsBFolder));
        HoldsFilms(fixture, _filmsFolder, _filmsBFolder);
        HoldsSeries(fixture);
        fixture.Server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = TestLibraries.ShowsB, Name = "Serie 4K", CollectionType = "tvshows" });
        fixture.Server.ImportedLibraryIds.Add(TestLibraries.ShowsB);
        var showsB = FederatedPaths.TypeFolderOf(_serverId, TestLibraries.ShowsB, TypeFolder.Series);
        fixture.Stubs.Setup(stubs => stubs.TypeFoldersOf(_serverId, TypeFolder.Series)).Returns([_showsFolder, showsB]);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
        Vanish(fixture, TestLibraries.ShowsB, "Serie 4K");
        HoldsFilms(fixture, _filmsFolder);
        HoldsSeries(fixture);
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        Assert.Equal(["Casa - Serie 4K"], fixture.Libraries.Removed.Select(call => call.Name));
        fixture.Scans.Steps.Clear();

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Single(fixture.Libraries.Removed);
        Assert.Single(fixture.Libraries.RemovedPaths);
        Assert.Empty(fixture.Scans.Steps);
        Assert.Contains(fixture.Libraries.Folders, folder => folder.Name == "Film 4K di casa");
        Assert.Empty(fixture.Renamer.Calls);
        var state = fixture.Manager.GetState(_serverId);
        Assert.Equal(AutomaticLibrariesError.None, state.Error);
        Assert.Empty(state.Conflicts);
        Assert.Equal(["Film 4K", "Serie 4K"], state.Vanished.Order());
    }

    [Fact]
    public void The_state_names_the_vanished_remote_libraries()
    {
        var fixture = OnTypeFolders();
        Vanish(fixture, TestLibraries.FilmsB, "Film 4K");

        Assert.Equal(["Film 4K"], fixture.Manager.GetState(_serverId).Vanished);
    }

    [Fact]
    public async Task The_folder_of_a_vanished_remote_library_is_deleted_once_no_library_points_at_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "jellymesh-vanished-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = new Fixture(root);
            fixture.Stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(false);
            fixture.Server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = TestLibraries.FilmsB, Name = "Film 4K", CollectionType = "movies" });
            fixture.Server.ImportedLibraryIds.Add(TestLibraries.FilmsB);
            HoldsFilms(fixture, _filmsFolder, _filmsBFolder);
            HoldsSeries(fixture);
            foreach (var folder in new[] { _filmsFolder, _filmsBFolder })
            {
                Directory.CreateDirectory(Path.Combine(root, folder));
                File.WriteAllBytes(Path.Combine(root, folder, FederatedPaths.HiddenFileName), []);
            }

            await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
            Vanish(fixture, TestLibraries.FilmsB, "Film 4K");
            HoldsFilms(fixture, _filmsFolder);

            await fixture.Manager.ReconcileAsync(CancellationToken.None);

            Assert.False(Directory.Exists(Path.Combine(root, FederatedPaths.LibraryFolder(_serverId, TestLibraries.FilmsB))));
            Assert.True(File.Exists(Path.Combine(root, _filmsFolder, FederatedPaths.HiddenFileName)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task The_folder_of_a_vanished_remote_library_stays_while_a_library_of_the_admin_points_at_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "jellymesh-vanished-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = new Fixture(root);
            fixture.Stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(false);
            fixture.Server.LibraryMode = LibraryMode.None;
            Directory.CreateDirectory(Path.Combine(root, _filmsBFolder));
            fixture.Libraries.Add("I miei film", CollectionTypeOptions.movies, Path.Combine(root, _filmsBFolder));
            HoldsFilms(fixture);
            HoldsSeries(fixture);
            fixture.Server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = TestLibraries.FilmsB, Name = "Film 4K", CollectionType = "movies" });
            Vanish(fixture, TestLibraries.FilmsB, "Film 4K");

            await fixture.Manager.ReconcileAsync(CancellationToken.None);

            Assert.True(Directory.Exists(Path.Combine(root, _filmsBFolder)));
            Assert.Empty(fixture.Libraries.RemovedPaths);

            fixture.Libraries.Find("I miei film").Locations = [];
            await fixture.Manager.ReconcileAsync(CancellationToken.None);

            Assert.False(Directory.Exists(Path.Combine(root, FederatedPaths.LibraryFolder(_serverId, TestLibraries.FilmsB))));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
