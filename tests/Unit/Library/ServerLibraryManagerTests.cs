using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class ServerLibraryManagerTests
{
    private const string Root = "/federata";
    private static readonly Guid _serverId = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000001");
    private static readonly string _movies = $"{Root}/{_serverId:D}/Film";
    private static readonly string _series = $"{Root}/{_serverId:D}/Serie";

    [Fact]
    public async Task Switching_on_creates_a_film_and_a_series_library_on_the_server_folders_with_the_safe_options()
    {
        var fixture = new Fixture();

        var request = fixture.Manager.Enable(_serverId);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.Equal(2, fixture.Libraries.Created.Count);
        var film = fixture.Libraries.Created.Single(call => call.Type == CollectionTypeOptions.movies);
        var series = fixture.Libraries.Created.Single(call => call.Type == CollectionTypeOptions.tvshows);
        Assert.Equal("Casa - Film", film.Name);
        Assert.Equal("Casa - Serie", series.Name);
        Assert.Equal([_movies], film.Options.PathInfos.Select(info => info.Path));
        Assert.Equal([_series], series.Options.PathInfos.Select(info => info.Path));
        foreach (var call in fixture.Libraries.Created)
        {
            Assert.False(call.Refresh);
            Assert.NotNull(call.Options.MetadataSavers);
            Assert.Empty(call.Options.MetadataSavers);
            Assert.Empty(call.Options.TypeOptions);
            Assert.Empty(call.Options.DisabledLocalMetadataReaders);
            Assert.False(call.Options.SaveLocalMetadata);
            Assert.False(call.Options.EnableRealtimeMonitor);
            Assert.True(call.Options.EnableAutomaticSeriesGrouping);
            Assert.False(call.Options.EnableChapterImageExtraction);
            Assert.False(call.Options.EnableTrickplayImageExtraction);
            Assert.False(call.Options.EnableLUFSScan);
            Assert.False(call.Options.AutomaticallyAddToCollection);
        }

        Assert.True(fixture.Server.AutomaticLibraries);
        fixture.Materializer.Verify(materializer => materializer.PrepareServerFolders(), Times.Once);
    }

    [Fact]
    public async Task With_collections_federated_the_libraries_are_created_adding_films_to_collections()
    {
        var fixture = new Fixture();
        fixture.Store.Configuration.FederateCollections = true;

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Equal(2, fixture.Libraries.Created.Count);
        Assert.All(fixture.Libraries.Created, call => Assert.True(call.Options.AutomaticallyAddToCollection));
    }

    [Fact]
    public async Task Both_libraries_are_created_before_one_scan_is_queued_after_the_running_one()
    {
        var fixture = new Fixture();
        fixture.Libraries.OnCreate = () => fixture.Scans.Steps.Add("create");

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Equal(["wait", "create", "create", "scan"], fixture.Scans.Steps);
    }

    [Fact]
    public async Task The_registry_keeps_the_name_and_id_Jellyfin_gave_found_by_path()
    {
        var fixture = new Fixture();
        fixture.Libraries.RenameOnCreate = name => name + "2";

        await fixture.Manager.Enable(_serverId).Job;

        var film = fixture.Server.AutomaticLibraryRegistry.Single(record => record.Kind == ServerLibraryKind.Movies);
        Assert.Equal("Casa - Film2", film.Name);
        Assert.Equal(fixture.Libraries.IdOf("Casa - Film2"), film.ItemId);
        Assert.Equal(["Casa - Film2", "Casa - Serie2"], fixture.Manager.GetState(_serverId).Libraries.Select(library => library.Name));
    }

    [Fact]
    public async Task A_name_already_taken_gets_the_first_digits_of_the_server_guid()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("casa - film", CollectionTypeOptions.movies, "/media/film");

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Equal(["Casa 1a2b3c4d - Film", "Casa - Serie"], fixture.Libraries.Created.Select(call => call.Name));
    }

    [Fact]
    public async Task The_name_is_checked_as_Jellyfin_will_store_it()
    {
        var fixture = new Fixture(serverName: "NAS: casa");
        fixture.Libraries.Add("NAS  casa - Film", CollectionTypeOptions.movies, "/media/film");

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Equal(["NAS  casa 1a2b3c4d - Film", "NAS  casa - Serie"], fixture.Libraries.Created.Select(call => call.Name));
    }

    [Fact]
    public async Task A_name_taken_even_with_the_digits_is_not_created_and_the_page_is_told()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Casa - Film", CollectionTypeOptions.movies, "/media/a");
        fixture.Libraries.Add("Casa 1a2b3c4d - Film", CollectionTypeOptions.movies, "/media/b");

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Created.Select(call => call.Name));
        Assert.Equal(AutomaticLibrariesError.NameTaken, fixture.Manager.GetState(_serverId).Error);
    }

    [Fact]
    public async Task A_type_already_in_the_registry_is_not_created_again()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Created.Clear();

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Empty(fixture.Libraries.Created);
    }

    [Fact]
    public void Without_a_federated_folder_the_switch_stays_off()
    {
        var fixture = new Fixture(root: string.Empty);

        var request = fixture.Manager.Enable(_serverId);

        Assert.Equal(AutomaticLibrariesOutcome.NoFederatedFolder, request.Outcome);
        Assert.False(fixture.Server.AutomaticLibraries);
        Assert.Empty(fixture.Libraries.Created);
    }

    [Fact]
    public void An_unknown_server_is_refused()
    {
        var fixture = new Fixture();

        Assert.Equal(AutomaticLibrariesOutcome.UnknownServer, fixture.Manager.Enable(Guid.NewGuid()).Outcome);
        Assert.Equal(AutomaticLibrariesOutcome.UnknownServer, fixture.Manager.Disable(Guid.NewGuid(), true).Outcome);
    }

    [Fact]
    public async Task A_failed_creation_is_reported_and_leaves_nothing_in_the_registry()
    {
        var fixture = new Fixture();
        fixture.Libraries.CreateFailure = new ArgumentException("The path does not exist.");

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Equal(AutomaticLibrariesError.CreateFailed, fixture.Manager.GetState(_serverId).Error);
    }

    [Fact]
    public async Task Switching_off_asks_for_a_confirmation_first_and_names_what_would_be_deleted()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;

        var request = fixture.Manager.Disable(_serverId, false);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.ConfirmationRequired, request.Outcome);
        Assert.Equal(["Casa - Film", "Casa - Serie"], request.ToDelete);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.True(fixture.Server.AutomaticLibraries);
    }

    [Fact]
    public async Task Switching_off_after_the_confirmation_deletes_the_registry_libraries_only()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Film", CollectionTypeOptions.movies, "/media/film");
        fixture.Libraries.Add("Mine on the folder", CollectionTypeOptions.movies, _movies);
        await fixture.Manager.Enable(_serverId).Job;

        var request = fixture.Manager.Disable(_serverId, true);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.All(fixture.Libraries.Removed, call => Assert.True(call.Refresh));
        Assert.Equal(["Film", "Mine on the folder"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.False(fixture.Server.AutomaticLibraries);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
    }

    [Fact]
    public async Task Switching_off_with_nothing_in_the_registry_needs_no_confirmation()
    {
        var fixture = new Fixture();
        fixture.Server.AutomaticLibraries = true;

        var request = fixture.Manager.Disable(_serverId, false);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.False(fixture.Server.AutomaticLibraries);
    }

    [Fact]
    public async Task Switching_off_deletes_a_library_the_admin_renamed_under_its_new_name()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");

        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(["Film di casa", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Empty(fixture.Libraries.Folders);
    }

    [Fact]
    public async Task A_new_library_under_the_old_name_elsewhere_makes_the_renamed_one_left_to_the_admin()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");
        fixture.Libraries.Add("Casa - Film", CollectionTypeOptions.movies, "/media/other");

        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Film di casa", "Casa - Film"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Equal(["Casa - Film"], fixture.Manager.GetState(_serverId).Released);
    }

    [Fact]
    public async Task A_library_the_admin_deleted_is_not_mistaken_for_two_others_on_the_same_folder()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        fixture.Libraries.Add("Also mine", CollectionTypeOptions.movies, _movies);

        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task A_library_already_there_at_switch_on_is_never_taken_for_a_renamed_one()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task A_library_already_there_at_switch_on_is_never_taken_by_the_name_of_one_in_the_registry()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));
        fixture.Server.AutomaticLibraryRegistry.Single(record => record.Kind == ServerLibraryKind.Movies).Name = "Mine";

        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task A_library_the_admin_made_on_the_server_folder_after_switch_on_is_never_taken_for_a_renamed_one()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Equal(["Casa - Film"], fixture.Server.ReleasedAutomaticLibraries);
        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task A_library_the_admin_made_on_the_server_folder_is_never_taken_for_a_renamed_one_of_a_removed_server()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task A_library_renamed_in_the_dashboard_is_still_followed_after_quiet_checks()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Film di casa", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Empty(fixture.Server.ReleasedAutomaticLibraries);
    }

    [Fact]
    public async Task Switching_on_again_still_follows_a_library_of_the_registry_renamed_in_the_dashboard()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");
        fixture.Libraries.Created.Clear();

        await fixture.Manager.Enable(_serverId).Job;
        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Equal(["Film di casa", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task Switching_off_leaves_to_the_admin_a_library_whose_path_or_type_changed()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Find("Casa - Film").Locations = [_movies, "/media/film"];
        fixture.Libraries.Find("Casa - Serie").CollectionType = CollectionTypeOptions.mixed;

        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Manager.GetState(_serverId).Released);
        Assert.False(fixture.Server.AutomaticLibraries);
    }

    [Fact]
    public async Task A_failed_deletion_keeps_the_switch_on_and_the_library_in_the_registry()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RemoveFailure = new IOException("busy");

        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.True(fixture.Server.AutomaticLibraries);
        Assert.Equal(2, fixture.Server.AutomaticLibraryRegistry.Count);
        Assert.Equal(AutomaticLibrariesError.RemoveFailed, fixture.Manager.GetState(_serverId).Error);
    }

    [Fact]
    public async Task The_sync_follows_a_library_renamed_in_the_dashboard()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RenameFromDashboard("Casa - Serie", "Serie di casa");

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        var series = fixture.Server.AutomaticLibraryRegistry.Single(record => record.Kind == ServerLibraryKind.Series);
        Assert.Equal("Serie di casa", series.Name);
        Assert.Equal(fixture.Libraries.IdOf("Serie di casa"), series.ItemId);
        Assert.Empty(fixture.Manager.GetState(_serverId).Released);
    }

    [Fact]
    public async Task The_sync_drops_a_library_whose_path_changed_and_says_so()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Find("Casa - Film").Locations = ["/media/film"];

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([ServerLibraryKind.Series], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Kind));
        Assert.Equal(["Casa - Film"], fixture.Manager.GetState(_serverId).Released);
    }

    [Fact]
    public async Task The_sync_drops_a_library_deleted_in_the_dashboard_and_does_not_create_it_again()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));
        fixture.Libraries.Created.Clear();

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([ServerLibraryKind.Series], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Kind));
        Assert.Empty(fixture.Libraries.Created);
        Assert.True(fixture.Server.AutomaticLibraries);
    }

    [Fact]
    public async Task Turning_the_switch_on_again_recreates_a_library_deleted_in_the_dashboard()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.Created.Clear();

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Equal(["Casa - Film"], fixture.Libraries.Created.Select(library => library.Name));
        Assert.Equal([ServerLibraryKind.Series, ServerLibraryKind.Movies], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Kind));
        Assert.Equal(2, fixture.Manager.GetState(_serverId).Libraries.Count);
    }

    [Fact]
    public async Task A_library_list_that_cannot_be_read_changes_nothing()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.ReadFailure = new IOException("unreadable");

        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(2, fixture.Server.AutomaticLibraryRegistry.Count);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.True(fixture.Server.AutomaticLibraries);
    }

    [Fact]
    public async Task Turning_the_switch_on_again_clears_the_old_notices()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Find("Casa - Film").Locations = ["/media/film"];
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        await fixture.Manager.Enable(_serverId).Job;

        Assert.Empty(fixture.Manager.GetState(_serverId).Released);
    }

    [Fact]
    public async Task While_a_job_runs_another_request_is_busy_and_the_sync_skips_the_follow_so_the_job_takes_no_renamed_one()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var off = fixture.Manager.Disable(_serverId, true);
        await fixture.Scans.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");

        Assert.Equal(AutomaticLibrariesOutcome.Busy, fixture.Manager.Enable(_serverId).Outcome);
        Assert.Equal(AutomaticLibrariesOutcome.Busy, fixture.Manager.Disable(_serverId, true).Outcome);
        Assert.True(fixture.Manager.GetState(_serverId).Busy);
        await fixture.Manager.ReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, entry => entry.Name == "Casa - Film");

        release.SetResult();
        await off.Job.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(fixture.Manager.GetState(_serverId).Busy);
        Assert.Equal(["Film di casa"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Equal(["Casa - Film"], fixture.Manager.GetState(_serverId).Released);
    }

    [Fact]
    public async Task Removing_the_server_deletes_the_libraries_of_its_registry_with_no_confirmation()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        fixture.Libraries.Add("Film", CollectionTypeOptions.movies, "/media/film");
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RenameFromDashboard("Casa - Serie", "Serie di casa");

        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Film", "Serie di casa"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.All(fixture.Libraries.Removed, call => Assert.True(call.Refresh));
        Assert.Equal(["Mine", "Film"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task Removing_the_server_leaves_to_the_admin_a_library_whose_path_or_type_changed()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Find("Casa - Film").Locations = [_movies, "/media/film"];
        fixture.Libraries.Find("Casa - Serie").CollectionType = CollectionTypeOptions.mixed;

        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_library_there_at_switch_on_is_not_deleted_with_the_removed_server()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task A_server_removed_while_its_library_is_being_created_leaves_no_library()
    {
        var fixture = new Fixture();
        fixture.Libraries.OnCreate = () =>
        {
            if (fixture.Store.Configuration.Servers.Count > 0)
            {
                fixture.RemoveServer();
                _ = fixture.Manager.DeleteRemovedServerLibraries();
            }
        };

        await fixture.Manager.Enable(_serverId).Job.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Manager.DeleteRemovedServerLibraries().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["Casa - Film"], fixture.Libraries.Created.Select(call => call.Name));
        Assert.Empty(fixture.Libraries.Folders);
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_removal_while_the_switch_off_waits_deletes_the_libraries_once_it_is_done()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var off = fixture.Manager.Disable(_serverId, true);
        await fixture.Scans.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.RemoveServer();
        var cleanup = fixture.Manager.DeleteRemovedServerLibraries();
        release.SetResult();
        await off.Job.WaitAsync(TimeSpan.FromSeconds(10));
        await cleanup.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Empty(fixture.Libraries.Folders);
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_failed_deletion_after_a_removal_is_tried_again_after_a_restart()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RemoveFailure = new IOException("busy");
        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();
        Assert.Equal(2, Assert.Single(fixture.Store.Configuration.RemovedServerLibraries).Libraries.Count);

        fixture.Libraries.RemoveFailure = null;
        var restarted = fixture.NewManager();
        await restarted.ReconcileAsync(CancellationToken.None);
        await restarted.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Folders);
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_server_added_back_keeps_its_new_libraries_and_deletes_those_it_left_even_if_there_at_switch_on()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RemoveFailure = new IOException("busy");
        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();
        fixture.Libraries.RemoveFailure = null;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));
        var back = fixture.AddServerBack("Casa");

        await fixture.Manager.Enable(_serverId).Job;
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Casa - Film", "Casa 1a2b3c4d - Serie"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Equal(["Casa - Film", "Casa 1a2b3c4d - Serie"], back.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_new_federated_folder_lists_as_released_the_libraries_a_removed_server_left()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RemoveFailure = new IOException("busy");
        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();
        fixture.Libraries.RemoveFailure = null;

        fixture.Store.Configuration.FederatedFolderPath = "/nuova";
        await fixture.Manager.DeleteRemovedServerLibraries();
        await fixture.NewManager().ReconcileAsync(CancellationToken.None);
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Folders.Select(folder => folder.Name));
        var held = Assert.Single(fixture.Store.Configuration.RemovedServerLibraries);
        Assert.Empty(held.Libraries);
        Assert.Equal(["Casa - Film", "Casa - Serie"], held.ReleasedLibraries);
    }

    [Fact]
    public async Task A_server_removed_after_a_new_federated_folder_lists_as_released_the_libraries_it_left()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Store.Configuration.FederatedFolderPath = "/nuova";

        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Removed);
        var held = Assert.Single(fixture.Store.Configuration.RemovedServerLibraries);
        Assert.Empty(held.Libraries);
        Assert.Equal(["Casa - Film", "Casa - Serie"], held.ReleasedLibraries);
    }

    [Fact]
    public async Task A_server_removed_twice_deletes_what_both_left()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RemoveFailure = new IOException("busy");
        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();
        fixture.Store.Configuration.Servers.Add(new RemoteServerConfig { Id = _serverId, Name = "Casa" });
        fixture.Libraries.RemoveFailure = null;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));
        fixture.Libraries.RemoveFailure = new IOException("busy");
        await fixture.Manager.Enable(_serverId).Job;
        Assert.Equal(["Casa - Serie", "Casa - Film", "Casa 1a2b3c4d - Serie"], fixture.Libraries.Folders.Select(folder => folder.Name));

        fixture.RemoveServer();
        fixture.Libraries.RemoveFailure = null;
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Folders);
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_server_removed_and_added_back_while_its_switch_on_waits_for_the_scans_gets_nothing_from_that_job()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, "/media/film");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var on = fixture.Manager.Enable(_serverId);
        await fixture.Scans.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        fixture.RemoveServer();
        var back = fixture.AddServerBack("Nuovo");
        release.SetResult();
        await on.Job.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Manager.DeleteRemovedServerLibraries().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(fixture.Libraries.Created);
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
        AssertUntouched(back);
        Assert.Equal(AutomaticLibrariesError.None, fixture.Manager.GetState(_serverId).Error);
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_server_removed_and_added_back_while_its_library_is_being_created_leaves_that_library_to_delete()
    {
        var fixture = new Fixture();
        RemoteServerConfig? back = null;
        fixture.Libraries.OnCreate = () =>
        {
            if (back is null)
            {
                fixture.RemoveServer();
                back = fixture.AddServerBack("Nuovo");
            }
        };

        await fixture.Manager.Enable(_serverId).Job.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Manager.DeleteRemovedServerLibraries().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["Casa - Film"], fixture.Libraries.Created.Select(call => call.Name));
        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Empty(fixture.Libraries.Folders);
        AssertUntouched(back!);
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_server_removed_and_added_back_before_its_switch_on_starts_gets_nothing_from_that_job()
    {
        var fixture = new Fixture();
        var other = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");
        fixture.Libraries.Add("Altro - Film", CollectionTypeOptions.movies, $"{Root}/{other:D}/Film");
        fixture.Store.Configuration.HoldAutomaticLibraries(
            other,
            [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Altro - Film", ItemId = fixture.Libraries.IdOf("Altro - Film") }],
            [],
            [],
            false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var cleanup = fixture.Manager.DeleteRemovedServerLibraries();
        await fixture.Scans.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var on = fixture.Manager.Enable(_serverId);

        fixture.RemoveServer();
        var back = fixture.AddServerBack("Nuovo");
        release.SetResult();
        await on.Job.WaitAsync(TimeSpan.FromSeconds(10));
        await cleanup.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AutomaticLibrariesOutcome.Started, on.Outcome);
        Assert.Empty(fixture.Libraries.Created);
        Assert.Equal(["Altro - Film"], fixture.Libraries.Removed.Select(call => call.Name));
        AssertUntouched(back);
    }

    [Fact]
    public async Task After_a_check_skipped_on_an_unreadable_library_list_the_next_one_takes_no_library_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();
        fixture.Libraries.ReadFailure = new IOException("unreadable");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.ReadFailure = null;

        await AssertMineIsNotAdopted(fixture);
    }

    [Fact]
    public async Task After_a_check_skipped_without_a_federated_folder_the_next_one_takes_no_library_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();
        fixture.Store.Configuration.FederatedFolderPath = string.Empty;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Store.Configuration.FederatedFolderPath = Root;

        await AssertMineIsNotAdopted(fixture);
    }

    [Fact]
    public async Task After_a_check_skipped_while_a_job_runs_the_next_one_takes_no_library_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();
        var other = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");
        fixture.Libraries.Add("Altro - Film", CollectionTypeOptions.movies, $"{Root}/{other:D}/Film");
        fixture.Store.Configuration.HoldAutomaticLibraries(
            other,
            [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Altro - Film", ItemId = fixture.Libraries.IdOf("Altro - Film") }],
            [],
            [],
            false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var cleanup = fixture.Manager.DeleteRemovedServerLibraries();
        await fixture.Scans.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Manager.ReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(10));

        await AssertMineIsNotAdopted(fixture);
    }

    [Fact]
    public async Task After_a_switch_off_stopped_without_a_federated_folder_the_next_one_takes_no_library_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();
        fixture.Store.Configuration.FederatedFolderPath = string.Empty;
        await fixture.Manager.Disable(_serverId, true).Job;
        Assert.Equal(AutomaticLibrariesError.ReadFailed, fixture.Manager.GetState(_serverId).Error);
        fixture.Store.Configuration.FederatedFolderPath = Root;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        await fixture.NewManager().Disable(_serverId, true).Job;

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task After_a_switch_on_stopped_on_an_unreadable_library_list_the_next_check_takes_no_library_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();
        fixture.Libraries.ReadFailure = new IOException("unreadable");
        await fixture.Manager.Enable(_serverId).Job;
        Assert.Equal(AutomaticLibrariesError.ReadFailed, fixture.Manager.GetState(_serverId).Error);
        fixture.Libraries.ReadFailure = null;

        await AssertMineIsNotAdopted(fixture);
    }

    [Fact]
    public async Task After_the_check_that_only_records_the_libraries_a_rename_is_followed_again()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.ReadFailure = new IOException("unreadable");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.ReadFailure = null;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Film di casa", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Empty(fixture.Server.ReleasedAutomaticLibraries);
    }

    [Fact]
    public async Task After_a_cleanup_stopped_on_an_unreadable_library_list_the_next_one_takes_no_library_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();
        fixture.Libraries.ReadFailure = new IOException("unreadable");
        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();
        fixture.Libraries.ReadFailure = null;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        await fixture.NewManager().DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task A_server_removed_after_a_skipped_check_leaves_libraries_whose_first_check_takes_none_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();
        fixture.Libraries.ReadFailure = new IOException("unreadable");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.ReadFailure = null;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task After_a_check_that_never_started_the_next_one_takes_no_library_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();

        fixture.Manager.MarkFollowSkipped();

        await AssertMineIsNotAdopted(fixture);
    }

    [Fact]
    public async Task After_a_check_that_never_started_a_removed_server_takes_no_library_for_a_renamed_one()
    {
        var fixture = await AdminLibraryAfterAQuietCheck();
        fixture.RemoveServer();

        fixture.Manager.MarkFollowSkipped();
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));
        await fixture.NewManager().DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task A_check_that_never_started_marks_every_server_and_removed_server_once()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.RemoveServer();
        var other = new RemoteServerConfig { Id = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002"), Name = "Altro" };
        fixture.Store.Update(config => config.Servers.Add(other));
        var saves = fixture.Store.Saves;

        fixture.Manager.MarkFollowSkipped();
        fixture.Manager.MarkFollowSkipped();

        Assert.True(other.FollowSkipped);
        Assert.True(Assert.Single(fixture.Store.Configuration.RemovedServerLibraries).FollowSkipped);
        Assert.Equal(saves + 1, fixture.Store.Saves);
    }

    [Fact]
    public async Task A_library_with_an_empty_id_is_never_taken_for_a_renamed_one_at_the_switch_off()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        fixture.Libraries.Find("Mine").ItemId = string.Empty;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        await fixture.Manager.Disable(_serverId, true).Job;

        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Equal(["Casa - Film"], fixture.Server.ReleasedAutomaticLibraries);
    }

    [Fact]
    public async Task A_check_while_a_library_has_no_id_is_skipped_and_logs_only_a_count()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");
        fixture.Libraries.Find("Film di casa").ItemId = null;

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Empty(fixture.Server.ReleasedAutomaticLibraries);
        Assert.True(fixture.Server.FollowSkipped);
        var line = Assert.Single(fixture.Logger.Lines, entry => entry.Contains("no id", StringComparison.Ordinal));
        Assert.Contains("1 ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Film di casa", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_renamed_without_a_refresh_is_followed_once_the_wait_gives_it_its_id()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");
        var renamed = fixture.Libraries.Find("Film di casa");
        renamed.ItemId = null;
        fixture.Scans.DuringReadyWait = () => renamed.ItemId = fixture.Libraries.IdOf("Film di casa");

        await fixture.Manager.WaitReadyAsync(CancellationToken.None);
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([ServerLibraryManager.ReadyLimit], fixture.Scans.ReadyWaits);
        Assert.Equal(["Film di casa", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Empty(fixture.Server.ReleasedAutomaticLibraries);
    }

    [Fact]
    public async Task With_no_library_to_follow_the_sync_does_not_wait_for_the_libraries()
    {
        var fixture = new Fixture();

        await fixture.Manager.WaitReadyAsync(CancellationToken.None);

        Assert.Empty(fixture.Scans.ReadyWaits);
    }

    [Fact]
    public async Task A_removed_server_never_deletes_a_library_a_configured_server_found_at_switch_on_and_lists_it_as_released()
    {
        var fixture = new Fixture();
        var other = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");
        fixture.Libraries.Add("Altro - Film", CollectionTypeOptions.movies, $"{Root}/{other:D}/Film");
        fixture.Server.PreexistingLibraryIds.Add(fixture.Libraries.IdOf("Altro - Film"));
        fixture.Store.Configuration.HoldAutomaticLibraries(
            other,
            [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Altro - Film", ItemId = fixture.Libraries.IdOf("Altro - Film") }],
            [],
            [],
            false);

        await fixture.Manager.DeleteRemovedServerLibraries();

        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(["Altro - Film"], fixture.Libraries.Folders.Select(folder => folder.Name));
        var held = Assert.Single(fixture.Store.Configuration.RemovedServerLibraries);
        Assert.Empty(held.Libraries);
        Assert.Equal(["Altro - Film"], held.ReleasedLibraries);
    }

    [Fact]
    public async Task A_library_a_removed_server_left_and_a_configured_server_found_at_switch_on_is_released_under_its_new_name()
    {
        var fixture = new Fixture();
        var other = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");
        fixture.Libraries.Add("Altro - Film", CollectionTypeOptions.movies, $"{Root}/{other:D}/Film");
        fixture.Libraries.Add("Altro - Serie", CollectionTypeOptions.tvshows, $"{Root}/{other:D}/Serie");
        fixture.Server.PreexistingLibraryIds.Add(fixture.Libraries.IdOf("Altro - Film"));
        fixture.Store.Configuration.HoldAutomaticLibraries(
            other,
            [
                new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Vecchio nome", ItemId = fixture.Libraries.IdOf("Altro - Film") },
                new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Altro - Serie", ItemId = fixture.Libraries.IdOf("Altro - Serie") }
            ],
            [],
            [],
            false);

        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal(["Altro - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Altro - Film"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Equal(["Altro - Film"], Assert.Single(fixture.Store.Configuration.RemovedServerLibraries).ReleasedLibraries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_held_library_a_configured_server_found_at_switch_on_and_the_admin_renamed_is_released_under_its_old_name(bool skipped)
    {
        var fixture = new Fixture();
        var other = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");
        fixture.Libraries.Add("Altro - Film", CollectionTypeOptions.movies, $"{Root}/{other:D}/Film");
        fixture.Server.PreexistingLibraryIds.Add(fixture.Libraries.IdOf("Altro - Film"));
        fixture.Store.Configuration.HoldAutomaticLibraries(
            other,
            [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Altro - Film", ItemId = fixture.Libraries.IdOf("Altro - Film") }],
            [],
            [fixture.Libraries.IdOf("Altro - Film")],
            skipped);
        fixture.Libraries.RenameFromDashboard("Altro - Film", "Film mio");

        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(["Film mio"], fixture.Libraries.Folders.Select(folder => folder.Name));
        var held = Assert.Single(fixture.Store.Configuration.RemovedServerLibraries);
        Assert.Empty(held.Libraries);
        Assert.Equal(["Altro - Film"], held.ReleasedLibraries);
    }

    [Fact]
    public async Task A_held_library_a_configured_server_found_at_switch_on_and_the_admin_renamed_is_never_taken_when_another_holds_its_folder()
    {
        var fixture = new Fixture();
        var other = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");
        fixture.Libraries.Add("Altro - Film", CollectionTypeOptions.movies, $"{Root}/{other:D}/Film");
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, $"{Root}/{other:D}/Film");
        fixture.Server.PreexistingLibraryIds.Add(fixture.Libraries.IdOf("Altro - Film"));
        fixture.Store.Configuration.HoldAutomaticLibraries(
            other,
            [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Altro - Film", ItemId = fixture.Libraries.IdOf("Altro - Film") }],
            [],
            [fixture.Libraries.IdOf("Altro - Film"), fixture.Libraries.IdOf("Mine")],
            false);
        fixture.Libraries.RenameFromDashboard("Altro - Film", "Film mio");

        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(["Film mio", "Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_held_library_the_server_added_back_found_at_switch_on_and_the_admin_renamed_is_never_deleted_next_to_its_new_library()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Add("Vecchia", CollectionTypeOptions.movies, _movies);
        fixture.Server.PreexistingLibraryIds.Add(fixture.Libraries.IdOf("Vecchia"));
        fixture.Store.Configuration.HoldAutomaticLibraries(
            _serverId,
            [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Vecchia", ItemId = fixture.Libraries.IdOf("Vecchia") }],
            [],
            [fixture.Libraries.IdOf("Vecchia")],
            false);
        fixture.Libraries.RenameFromDashboard("Vecchia", "Film mio");

        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(["Casa - Film", "Casa - Serie", "Film mio"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_removed_server_never_takes_for_a_renamed_one_a_library_a_configured_server_found_at_switch_on()
    {
        var fixture = new Fixture();
        var other = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, $"{Root}/{other:D}/Film");
        fixture.Server.PreexistingLibraryIds.Add(fixture.Libraries.IdOf("Mine"));
        fixture.Store.Configuration.HoldAutomaticLibraries(
            other,
            [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Altro - Film", ItemId = fixture.Libraries.IdOf("Altro - Film") }],
            [],
            [],
            false);

        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_removed_server_whose_libraries_existed_when_another_server_was_switched_on_deletes_them_but_never_an_admin_library_on_that_folder()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        var other = new RemoteServerConfig { Id = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002"), Name = "Altro" };
        fixture.Store.Update(config => config.Servers.Add(other));
        await fixture.Manager.Enable(other.Id).Job;
        Assert.DoesNotContain(fixture.Libraries.IdOf("Casa - Film"), other.PreexistingLibraryIds);
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);

        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Altro - Film", "Altro - Serie", "Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task Removing_a_server_whose_libraries_existed_when_another_server_was_switched_on_deletes_them()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Film", CollectionTypeOptions.movies, "/media/film");
        await fixture.Manager.Enable(_serverId).Job;
        var other = new RemoteServerConfig { Id = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002"), Name = "Altro" };
        fixture.Store.Update(config => config.Servers.Add(other));
        await fixture.Manager.Enable(other.Id).Job;

        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Equal([fixture.Libraries.IdOf("Film")], other.PreexistingLibraryIds);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["Film", "Altro - Film", "Altro - Serie"], fixture.Libraries.Folders.Select(folder => folder.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task A_library_a_removed_server_left_is_never_recorded_as_there_at_switch_on()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RemoveFailure = new IOException("busy");
        fixture.RemoveServer();
        await fixture.Manager.DeleteRemovedServerLibraries();
        fixture.Libraries.RemoveFailure = null;
        var other = new RemoteServerConfig { Id = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002"), Name = "Altro" };
        fixture.Store.Update(config => config.Servers.Add(other));

        await fixture.Manager.Enable(other.Id).Job;
        await fixture.Manager.DeleteRemovedServerLibraries();

        Assert.Empty(other.PreexistingLibraryIds);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    // Switched on, then an admin library made on the server Film folder and seen by one check.
    private static async Task<Fixture> AdminLibraryAfterAQuietCheck()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.Libraries.Add("Mine", CollectionTypeOptions.movies, _movies);
        return fixture;
    }

    // The registry Film library disappears: the next check, after a restart, does not take the admin library for it.
    private static async Task AssertMineIsNotAdopted(Fixture fixture)
    {
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Equal(["Casa - Film"], fixture.Server.ReleasedAutomaticLibraries);
        await fixture.Manager.Disable(_serverId, true).Job;
        Assert.Equal(["Mine"], fixture.Libraries.Folders.Select(folder => folder.Name));
    }

    // A server added back is a new one: a job started for the one removed writes nothing in it.
    private static void AssertUntouched(RemoteServerConfig server)
    {
        Assert.False(server.AutomaticLibraries);
        Assert.Empty(server.AutomaticLibraryRegistry);
        Assert.Empty(server.ReleasedAutomaticLibraries);
        Assert.Empty(server.PreexistingLibraryIds);
    }

    private sealed class Fixture
    {
        public Fixture(string root = Root, string serverName = "Casa")
        {
            Server = new RemoteServerConfig { Id = _serverId, Name = serverName };
            Store.Configuration.FederatedFolderPath = root;
            Store.Configuration.Servers.Add(Server);
            var fileSystem = new Mock<IFileSystem>();
            fileSystem.Setup(system => system.GetValidFilename(It.IsAny<string>()))
                .Returns<string>(name => string.Concat(name.Select(character => ":/\\*?\"<>|".Contains(character, StringComparison.Ordinal) ? ' ' : character)));
            FileSystem = fileSystem.Object;
            Manager = NewManager();
        }

        public IFileSystem FileSystem { get; }

        public InMemoryConfigurationStore Store { get; } = new();

        public RemoteServerConfig Server { get; }

        public FakeLibraries Libraries { get; } = new();

        public RecordingScans Scans { get; } = new();

        public Mock<ILibraryMaterializer> Materializer { get; } = new();

        public ServerLibraryManager Manager { get; }

        public CapturingLogger<ServerLibraryManager> Logger { get; } = new();

        public ServerLibraryManager NewManager() => new(Store, Libraries.Object, FileSystem, Scans, Materializer.Object, Logger);

        // Like the registry: the server leaves the configuration and its libraries are held in the same save.
        public void RemoveServer() => Store.Update(config =>
        {
            var server = config.Servers.Single(candidate => candidate.Id == _serverId);
            config.Servers.Remove(server);
            config.HoldAutomaticLibraries(server.Id, server.AutomaticLibraryRegistry, server.PreexistingLibraryIds, server.FollowedLibraryIds, server.FollowSkipped);
        });

        // Like the registry for a remote that comes back: a new server with the same id.
        public RemoteServerConfig AddServerBack(string name)
        {
            var server = new RemoteServerConfig { Id = _serverId, Name = name };
            Store.Update(config => config.Servers.Add(server));
            return server;
        }
    }

    internal sealed record CreateCall(string Name, CollectionTypeOptions? Type, LibraryOptions Options, bool Refresh);

    internal sealed record RemoveCall(string Name, bool Refresh);

    // Strict: any library call other than these fails the test, so nothing outside the registry is touched.
    internal sealed class FakeLibraries
    {
        private readonly Mock<ILibraryManager> _mock = new(MockBehavior.Strict);

        public FakeLibraries()
        {
            _mock.Setup(manager => manager.GetVirtualFolders()).Returns(() =>
            {
                if (ReadFailure is not null)
                {
                    throw ReadFailure;
                }

                return Folders.Select(Copy).ToList();
            });
            _mock.Setup(manager => manager.AddVirtualFolder(It.IsAny<string>(), It.IsAny<CollectionTypeOptions?>(), It.IsAny<LibraryOptions>(), It.IsAny<bool>()))
                .Returns<string, CollectionTypeOptions?, LibraryOptions, bool>((name, type, options, refresh) =>
                {
                    Created.Add(new CreateCall(name, type, options, refresh));
                    OnCreate?.Invoke();
                    if (CreateFailure is not null)
                    {
                        throw CreateFailure;
                    }

                    Add(RenameOnCreate?.Invoke(name) ?? name, type, options.PathInfos.Select(info => info.Path).ToArray());
                    return Task.CompletedTask;
                });
            _mock.Setup(manager => manager.RemoveVirtualFolder(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns<string, bool>((name, refresh) =>
                {
                    if (RemoveFailure is not null)
                    {
                        throw RemoveFailure;
                    }

                    Removed.Add(new RemoveCall(name, refresh));
                    Folders.Remove(Find(name));
                    return Task.CompletedTask;
                });
        }

        public ILibraryManager Object => _mock.Object;

        public List<VirtualFolderInfo> Folders { get; } = [];

        public List<CreateCall> Created { get; } = [];

        public List<RemoveCall> Removed { get; } = [];

        public Func<string, string>? RenameOnCreate { get; set; }

        public Action? OnCreate { get; set; }

        public Exception? CreateFailure { get; set; }

        public Exception? RemoveFailure { get; set; }

        public Exception? ReadFailure { get; set; }

        public void Add(string name, CollectionTypeOptions? type, params string[] locations)
            => Folders.Add(new VirtualFolderInfo { Name = name, CollectionType = type, Locations = locations, ItemId = IdOf(name) });

        public VirtualFolderInfo Find(string name) => Folders.Single(folder => folder.Name == name);

        // Like Jellyfin, the id comes from the name, so a rename changes both.
        public string IdOf(string name)
            => new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant()))).ToString("N");

        public void RenameFromDashboard(string name, string newName)
        {
            var folder = Find(name);
            folder.Name = newName;
            folder.ItemId = IdOf(newName);
        }

        private static VirtualFolderInfo Copy(VirtualFolderInfo folder) => new()
        {
            Name = folder.Name,
            CollectionType = folder.CollectionType,
            Locations = folder.Locations.ToArray(),
            ItemId = folder.ItemId
        };
    }

    internal sealed class RecordingScans : ILibraryScans
    {
        public List<string> Steps { get; } = [];

        public TaskCompletionSource Entered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Block { get; set; } = Task.CompletedTask;

        public Task WaitIdleAsync(CancellationToken cancellationToken)
        {
            if (Steps.LastOrDefault() != "wait")
            {
                Steps.Add("wait");
            }

            Entered.TrySetResult();
            return Block;
        }

        public Task QueueAndWaitAsync(CancellationToken cancellationToken)
        {
            Steps.Add("scan");
            return Task.CompletedTask;
        }

        public List<TimeSpan> ReadyWaits { get; } = [];

        public Action? DuringReadyWait { get; set; }

        public Task<bool> WaitReadyAsync(TimeSpan limit, CancellationToken cancellationToken)
        {
            ReadyWaits.Add(limit);
            DuringReadyWait?.Invoke();
            return Task.FromResult(true);
        }
    }
}
