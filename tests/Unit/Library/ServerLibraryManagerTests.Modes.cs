using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The libraries each library mode wants, on the remote library layout (§10 row 437).
public sealed partial class ServerLibraryManagerTests
{
    private static readonly string _showsFolder = FederatedPaths.TypeFolderOf(_serverId, TestLibraries.Shows, TypeFolder.Series);

    // A server out of the 0.5 layout, with a film library 4K next to its film and series libraries.
    private static Fixture OnTypeFolders()
    {
        var fixture = new Fixture();
        fixture.Stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(false);
        fixture.Server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = TestLibraries.FilmsB, Name = "Film 4K", CollectionType = "movies" });
        fixture.Server.ImportedLibraryIds.Add(TestLibraries.FilmsB);
        return fixture;
    }

    [Fact]
    public async Task By_type_creates_a_library_only_for_the_types_present_on_their_type_folders_with_one_scan()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);

        var request = fixture.Manager.Enable(_serverId, LibraryMode.ByType);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        var created = Assert.Single(fixture.Libraries.Created);
        Assert.Equal(("Casa - Film", CollectionTypeOptions.movies, false), (created.Name, created.Type, created.Refresh));
        Assert.Equal([Absolute(_filmsFolder), Absolute(_filmsBFolder)], created.Options.PathInfos.Select(info => info.Path));
        Assert.Equal(LibraryMode.ByType, fixture.Server.LibraryMode);
        Assert.Single(fixture.Scans.Steps, step => step == "scan");
    }

    [Fact]
    public async Task By_remote_library_names_each_library_after_its_remote_library()
    {
        var fixture = OnTypeFolders();

        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;

        Assert.Equal(
            [
                ("Casa - Film", CollectionTypeOptions.movies, Absolute(_filmsFolder)),
                ("Casa - Film 4K", CollectionTypeOptions.movies, Absolute(_filmsBFolder)),
                ("Casa - Serie", CollectionTypeOptions.tvshows, Absolute(_showsFolder))
            ],
            fixture.Libraries.Created.Select(call => (call.Name, call.Type, Assert.Single(call.Options.PathInfos).Path)).OrderBy(call => call.Name));
        Assert.Equal(
            [TestLibraries.Shows, TestLibraries.Films, TestLibraries.FilmsB],
            fixture.Server.AutomaticLibraryRegistry.Select(record => record.RemoteLibraryId).Order(StringComparer.Ordinal));
        Assert.Single(fixture.Scans.Steps, step => step == "scan");
    }

    [Fact]
    public async Task A_remote_library_renamed_on_its_server_renames_its_library_with_the_server()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        fixture.Server.RemoteLibraries.Single(library => library.Id == TestLibraries.FilmsB).Name = "UHD";
        fixture.Server.Name = "Mare";

        Assert.True(await fixture.Manager.RenameAsync(_serverId, CancellationToken.None));

        Assert.Equal(["Mare - Film", "Mare - Serie", "Mare - UHD"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name).Order());
    }

    [Fact]
    public async Task None_is_not_a_switch_on_and_a_server_in_none_mode_gets_no_library()
    {
        var fixture = OnTypeFolders();

        var request = fixture.Manager.Enable(_serverId, LibraryMode.None);
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(AutomaticLibrariesOutcome.ModeChangeNotAllowed, request.Outcome);
        Assert.Equal(LibraryMode.None, fixture.Server.LibraryMode);
        Assert.Empty(fixture.Libraries.Created);
    }

    [Fact]
    public void In_none_mode_the_state_lists_the_folders_to_add_by_hand()
    {
        var fixture = OnTypeFolders();

        var state = fixture.Manager.GetState(_serverId);

        Assert.Equal(LibraryMode.None, state.Mode);
        Assert.Equal(
            [
                new ManualLibraryPath(Absolute(_filmsFolder), "movies", "Film"),
                new ManualLibraryPath(Absolute(_showsFolder), "tvshows", "Serie"),
                new ManualLibraryPath(Absolute(_filmsBFolder), "movies", "Film 4K")
            ],
            state.ManualPaths);
    }

    [Fact]
    public async Task A_library_the_admin_deleted_is_not_made_again_by_a_check()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, fixture.Libraries.Created.Count);
        Assert.Equal(["Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
    }

    [Fact]
    public async Task The_state_says_a_library_is_missing_only_while_one_the_mode_wants_is_not_there()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        Assert.False(fixture.Manager.GetState(_serverId).Missing);
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film 4K"));

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.True(fixture.Manager.GetState(_serverId).Missing);
    }

    [Fact]
    public void In_none_mode_no_library_is_missing()
    {
        Assert.False(OnTypeFolders().Manager.GetState(_serverId).Missing);
    }

    [Fact]
    public async Task A_type_that_comes_with_a_new_remote_library_gets_its_library_at_the_next_check()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.Shows);
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Server.ImportedLibraryIds.Add(TestLibraries.Shows);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Created.Select(call => call.Name));
        Assert.Equal([Absolute(_showsFolder)], fixture.Libraries.Find("Casa - Serie").Locations);
    }

    [Fact]
    public async Task Another_mode_is_refused_while_the_server_has_libraries()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId).Job;

        var request = fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.ModeChangeNotAllowed, request.Outcome);
        Assert.Equal(LibraryMode.ByType, fixture.Server.LibraryMode);
        Assert.Equal(2, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task Out_of_the_older_layout_a_library_drops_the_server_folder_for_the_type_folders()
    {
        var fixture = OnTypeFolders();
        fixture.Stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(true);
        await fixture.Manager.Enable(_serverId).Job;
        Assert.Equal([_movies], fixture.Libraries.Find("Casa - Film").Locations);
        var kept = fixture.Server.AutomaticLibraryRegistry.Select(library => (library.Name, library.ItemId)).ToList();
        fixture.Stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns(false);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        // Same libraries, with the same names and ids: only their paths change (§10 row 445).
        Assert.Equal([Absolute(_filmsFolder), Absolute(_filmsBFolder)], fixture.Libraries.Find("Casa - Film").Locations);
        Assert.Equal([("Casa - Film", _movies), ("Casa - Serie", _series)], fixture.Libraries.RemovedPaths);
        Assert.Equal(kept, fixture.Server.AutomaticLibraryRegistry.Select(library => (library.Name, library.ItemId)));
        Assert.Equal(2, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task While_the_layout_in_use_is_unknown_no_library_is_made()
    {
        var fixture = OnTypeFolders();
        fixture.Stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns((bool?)null);

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Equal(LibraryMode.ByType, fixture.Server.LibraryMode);
    }

    [Fact]
    public async Task A_server_added_in_a_mode_before_the_federated_folder_was_set_gets_its_libraries_at_a_check()
    {
        var fixture = OnTypeFolders();
        fixture.Server.LibraryMode = LibraryMode.ByRemoteLibrary;

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film", "Casa - Film 4K", "Casa - Serie"], fixture.Libraries.Created.Select(call => call.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_remote_library_kept_twice_does_not_stop_a_rename_of_the_server()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        fixture.Server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = TestLibraries.FilmsB, Name = "Film 4K bis", CollectionType = "movies" });
        fixture.Server.Name = "Mare";

        Assert.True(await fixture.Manager.RenameAsync(_serverId, CancellationToken.None));

        Assert.Equal(["Mare - Film", "Mare - Film 4K", "Mare - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_remote_library_name_too_long_for_a_folder_is_cut_without_splitting_a_character()
    {
        var fixture = OnTypeFolders();
        fixture.Server.RemoteLibraries.Single(library => library.Id == TestLibraries.FilmsB).Name = "Film " + string.Concat(Enumerable.Repeat("é🎬", 200));

        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;

        var name = fixture.Libraries.Created.Select(call => call.Name).Single(name => name.StartsWith("Casa - Film é", StringComparison.Ordinal));
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(name), 100, 200);
        Assert.DoesNotContain('\uFFFD', name);
        Assert.False(char.IsHighSurrogate(name[^1]));
    }
}
