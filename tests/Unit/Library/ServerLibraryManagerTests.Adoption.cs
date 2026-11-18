using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// An admin library with exactly the paths and type the plugin would give is adopted; a partial match is neither made nor
// adopted, and the state says why (§10 rows 440-441, D4, D30).
public sealed partial class ServerLibraryManagerTests
{
    private static string[] FilmPaths => [Absolute(_filmsFolder), Absolute(_filmsBFolder)];

    [Fact]
    public async Task An_admin_library_with_exactly_the_paths_and_type_is_adopted_instead_of_made()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Created.Select(call => call.Name));
        var adopted = Assert.Single(fixture.Server.AutomaticLibraryRegistry, record => record.Kind == ServerLibraryKind.Movies);
        Assert.Equal(("Film di casa", fixture.Libraries.IdOf("Film di casa"), true), (adopted.Name, adopted.ItemId, adopted.Adopted));
        Assert.DoesNotContain(fixture.Libraries.IdOf("Film di casa"), fixture.Server.PreexistingLibraryIds);
        var state = fixture.Manager.GetState(_serverId);
        Assert.Contains(state.Libraries, library => library is { Name: "Film di casa", Adopted: true });
        Assert.Contains(state.Libraries, library => library is { Name: "Casa - Serie", Adopted: false });
        Assert.Empty(state.Conflicts);
    }

    [Fact]
    public async Task An_admin_library_that_also_holds_folders_of_other_servers_does_not_block_the_server_library()
    {
        var fixture = OnTypeFolders();
        var other = Absolute(FederatedPaths.TypeFolderOf(Guid.NewGuid(), TestLibraries.Films, TypeFolder.Movies));
        fixture.Libraries.Add("Tutti i film", CollectionTypeOptions.movies, [Absolute(_filmsFolder), other]);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Created.Select(call => call.Name).Order());
        Assert.Empty(fixture.Manager.GetState(_serverId).Conflicts);
        Assert.DoesNotContain(fixture.Server.AutomaticLibraryRegistry, record => record.Adopted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/media/\0film")]
    public async Task An_admin_library_with_a_location_that_is_no_path_does_not_stop_the_check(string location)
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Strana", CollectionTypeOptions.movies, [location.Replace("\\0", "\0", StringComparison.Ordinal)]);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record is { Name: "Film di casa", Adopted: true });
        Assert.Equal(["Casa - Serie"], fixture.Libraries.Created.Select(call => call.Name));
    }

    [Fact]
    public async Task An_adopted_library_stays_in_the_registry_after_a_restart_and_its_checks()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        var manager = fixture.NewManager();
        await manager.ReconcileAsync(CancellationToken.None);
        await manager.ReconcileAsync(CancellationToken.None);

        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record is { Name: "Film di casa", Adopted: true });
        Assert.Empty(fixture.Server.ReleasedAutomaticLibraries);
        Assert.Single(fixture.Libraries.Created);
    }

    [Fact]
    public async Task An_adopted_library_takes_the_folder_of_a_new_remote_library_of_its_type()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = TestLibraries.FilmsC, Name = "Film 8K", CollectionType = "movies" });
        fixture.Server.ImportedLibraryIds.Add(TestLibraries.FilmsC);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        var filmsC = Absolute(FederatedPaths.TypeFolderOf(_serverId, TestLibraries.FilmsC, TypeFolder.Movies));
        Assert.Equal([("Film di casa", filmsC)], fixture.Libraries.AddedPaths);
        Assert.Equal([.. FilmPaths, filmsC], fixture.Libraries.Find("Film di casa").Locations);
    }

    [Fact]
    public async Task An_adopted_library_is_never_renamed_with_its_server()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Server.Name = "Mare";

        Assert.True(await fixture.Manager.RenameAsync(_serverId, CancellationToken.None));

        Assert.Equal([("Casa - Serie", "Mare - Serie")], fixture.Renamer.Calls);
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record is { Name: "Film di casa", Adopted: true });
    }

    [Fact]
    public async Task Switching_off_releases_an_adopted_library_by_removing_only_its_paths()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        var asked = fixture.Manager.Disable(_serverId, false);
        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(["Casa - Serie"], asked.ToDelete);
        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(FilmPaths.Select(path => ("Film di casa", path)), fixture.Libraries.RemovedPaths);
        Assert.Empty(fixture.Libraries.Find("Film di casa").Locations);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Empty(fixture.Server.ReleasedAutomaticLibraries);
        Assert.False(fixture.Server.AutomaticLibraries);
    }

    [Fact]
    public async Task With_only_an_adopted_library_switching_off_needs_no_confirmation()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        var request = fixture.Manager.Disable(_serverId, false);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Contains(fixture.Libraries.Folders, folder => folder.Name == "Film di casa");
    }

    [Fact]
    public async Task Releasing_an_adopted_library_scans_so_the_titles_of_the_server_leave_it()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Scans.Steps.Clear();

        await fixture.Manager.Disable(_serverId, false).Job;

        Assert.Equal("scan", fixture.Scans.Steps.LastOrDefault());
    }

    [Fact]
    public async Task Switching_off_clears_what_was_in_the_way()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Tutto", CollectionTypeOptions.movies, Root);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        Assert.NotEmpty(fixture.Manager.GetState(_serverId).Conflicts);

        await fixture.Manager.Disable(_serverId, false).Job;

        Assert.Empty(fixture.Manager.GetState(_serverId).Conflicts);
    }

    [Fact]
    public async Task Removing_the_server_releases_an_adopted_library_once_its_titles_are_gone()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.RemoveServer();
        fixture.Stubs.Setup(stubs => stubs.HasTitlesOf(_serverId)).Returns(true);

        await fixture.Manager.DeleteRemovedServerLibraries();
        Assert.Empty(fixture.Libraries.RemovedPaths);
        fixture.Stubs.Setup(stubs => stubs.HasTitlesOf(_serverId)).Returns(false);
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(FilmPaths.Select(path => ("Film di casa", path)), fixture.Libraries.RemovedPaths);
        Assert.Contains(fixture.Libraries.Folders, folder => folder.Name == "Film di casa");
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_library_on_a_parent_folder_is_neither_made_nor_adopted_and_the_state_says_so()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Tutto", CollectionTypeOptions.movies, $"{Root}/{_serverId:D}");

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Empty(fixture.Libraries.AddedPaths);
        var conflict = Assert.Single(fixture.Manager.GetState(_serverId).Conflicts);
        Assert.Equal(("Tutto", LibraryConflictReason.ParentPath, ServerLibraryKind.Movies, string.Empty), (conflict.Library, conflict.Reason, conflict.Kind, conflict.RemoteLibraryId));
        Assert.Equal(FilmPaths, conflict.Paths);
    }

    [Fact]
    public async Task A_library_of_another_type_on_the_paths_is_neither_made_nor_adopted()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film come serie", CollectionTypeOptions.tvshows, FilmPaths);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Equal(("Film come serie", LibraryConflictReason.OtherType), Assert.Single(fixture.Manager.GetState(_serverId).Conflicts) is var conflict ? (conflict.Library, conflict.Reason) : default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_library_with_other_paths_or_only_part_of_them_is_neither_made_nor_adopted(bool extra)
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film miei", CollectionTypeOptions.movies, extra ? [.. FilmPaths, "/media/film"] : [FilmPaths[0]]);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Empty(fixture.Libraries.AddedPaths);
        Assert.Equal(("Film miei", LibraryConflictReason.OtherPaths), Assert.Single(fixture.Manager.GetState(_serverId).Conflicts) is var conflict ? (conflict.Library, conflict.Reason) : default);
    }

    [Fact]
    public async Task Two_admin_libraries_with_exactly_the_paths_are_neither_adopted_nor_joined_by_a_third()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film A", CollectionTypeOptions.movies, FilmPaths);
        fixture.Libraries.Add("Film B", CollectionTypeOptions.movies, FilmPaths);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Equal(
            [("Film A", LibraryConflictReason.SamePaths), ("Film B", LibraryConflictReason.SamePaths)],
            fixture.Manager.GetState(_serverId).Conflicts.Select(conflict => (conflict.Library, conflict.Reason)));
    }

    [Fact]
    public async Task Once_the_admin_fixes_a_partial_match_the_next_check_makes_the_library()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Tutto", CollectionTypeOptions.movies, Root);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Tutto"));

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film"], fixture.Libraries.Created.Select(call => call.Name));
        Assert.Empty(fixture.Manager.GetState(_serverId).Conflicts);
    }

    [Fact]
    public async Task An_exact_library_with_no_id_yet_waits_for_a_later_round_and_is_then_adopted()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        fixture.Libraries.Find("Film di casa").ItemId = null;

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        Assert.Empty(fixture.Libraries.Created);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        fixture.Libraries.Find("Film di casa").ItemId = fixture.Libraries.IdOf("Film di casa");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Libraries.Created);
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record is { Name: "Film di casa", Adopted: true });
    }

    [Fact]
    public async Task An_admin_library_made_after_the_switch_on_is_adopted_by_the_check_that_needs_it()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.Add("Serie di casa", CollectionTypeOptions.tvshows, Absolute(_showsFolder));
        fixture.Server.ImportedLibraryIds.Add(TestLibraries.Shows);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film"], fixture.Libraries.Created.Select(call => call.Name));
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record is { Name: "Serie di casa", Kind: ServerLibraryKind.Series, Adopted: true });
    }

    [Fact]
    public async Task A_library_a_removed_server_left_is_never_adopted_by_the_server_added_back()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.RemoveFailure = new InvalidOperationException("busy");
        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();
        fixture.Libraries.RemoveFailure = null;
        var back = fixture.AddServerBack("Casa");
        back.RemoteLibraries.Add(new RemoteLibraryConfig { Id = TestLibraries.FilmsB, Name = "Film 4K", CollectionType = "movies" });
        back.ImportedLibraryIds.Remove(TestLibraries.Shows);
        back.ImportedLibraryIds.Add(TestLibraries.FilmsB);

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.DoesNotContain(back.AutomaticLibraryRegistry, record => record.Adopted);
        Assert.Equal(2, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task A_switched_off_library_with_exactly_the_paths_is_adopted_and_the_state_says_it_is_off()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        fixture.Libraries.Find("Film di casa").LibraryOptions = new LibraryOptions { Enabled = false };

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        var library = Assert.Single(fixture.Manager.GetState(_serverId).Libraries);
        Assert.Equal(("Film di casa", true, true), (library.Name, library.Adopted, library.Disabled));
    }

    [Fact]
    public async Task An_adopted_library_the_admin_gives_another_folder_stays_adopted_and_keeps_that_folder_on_release()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.Find("Film di casa").Locations = [.. FilmPaths, "/media/film"];

        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record is { Name: "Film di casa", Adopted: true });
        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(FilmPaths.Select(path => ("Film di casa", path)), fixture.Libraries.RemovedPaths);
        Assert.Equal(["/media/film"], fixture.Libraries.Find("Film di casa").Locations);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Libraries.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_adopted_library_with_no_folder_of_the_server_or_another_type_is_left_to_the_admin_untouched(bool otherType)
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        if (otherType)
        {
            fixture.Libraries.Find("Film di casa").CollectionType = CollectionTypeOptions.tvshows;
        }
        else
        {
            fixture.Libraries.Find("Film di casa").Locations = ["/media/film"];
        }

        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Empty(fixture.Libraries.RemovedPaths);
        Assert.Empty(fixture.Libraries.AddedPaths);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Libraries.Created);
    }
}
