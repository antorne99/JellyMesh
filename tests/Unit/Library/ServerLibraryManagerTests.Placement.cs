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

// The follow before the placement of new servers, and the automatic libraries found in that same read (§10 rows 384, 385).
public sealed partial class ServerLibraryManagerTests
{
    [Fact]
    public async Task Placement_follows_a_library_renamed_in_the_dashboard_and_counts_it_as_automatic()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Equal(["Casa - Serie", "Film di casa"], libraries.Automatic.Select(folder => folder.Name).Order(StringComparer.Ordinal));
        Assert.All(libraries.Automatic, folder => Assert.Contains(folder, libraries.Folders));
        var record = fixture.Server.AutomaticLibraryRegistry.Single(record => record.Kind == ServerLibraryKind.Movies);
        Assert.Equal("Film di casa", record.Name);
        Assert.Equal(fixture.Libraries.IdOf("Film di casa"), record.ItemId);
    }

    [Fact]
    public async Task Placement_follows_a_rename_resumed_at_start_with_no_follow_after_it()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Renamer.HasPending = true;
        fixture.Renamer.ResumeFinishes = true;
        fixture.Renamer.OnResume = () => fixture.Libraries.RenameFromDashboard("Casa - Film", "Mare - Film");
        await fixture.Manager.ResumeRenamesAsync(CancellationToken.None);
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record.Name == "Casa - Film");

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Contains(libraries.Automatic, folder => folder.Name == "Mare - Film");
        Assert.Equal(["Mare - Film", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
    }

    [Fact]
    public async Task Placement_waits_while_a_library_has_no_id_and_marks_no_check_skipped()
    {
        // The follow cannot match a library with no id (decision 213); a skipped mark would lose the rename (decision 173).
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");
        fixture.Libraries.Find("Film di casa").ItemId = null;

        Assert.Null(fixture.Manager.FollowForPlacement());
        Assert.False(fixture.Server.FollowSkipped);
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record.Name == "Casa - Film");

        fixture.Libraries.Find("Film di casa").ItemId = fixture.Libraries.IdOf("Film di casa");
        Assert.NotNull(fixture.Manager.FollowForPlacement());
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record.Name == "Film di casa");
    }

    [Fact]
    public void With_no_automatic_library_a_library_with_no_id_does_not_hold_placement_back()
    {
        var fixture = new Fixture();
        fixture.Libraries.Add("Film", CollectionTypeOptions.movies, "/media/film");
        fixture.Libraries.Find("Film").ItemId = null;

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Empty(libraries.Automatic);
    }

    [Fact]
    public async Task Placement_waits_when_the_libraries_cannot_be_read()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.ReadFailure = new IOException("/secret/path");

        Assert.Null(fixture.Manager.FollowForPlacement());
        Assert.False(fixture.Server.FollowSkipped);
    }

    [Fact]
    public async Task Placement_waits_while_a_job_changes_the_libraries()
    {
        var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var job = fixture.Manager.Enable(_serverId).Job;
        await fixture.Scans.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(fixture.Manager.FollowForPlacement());

        release.SetResult();
        await job.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(fixture.Manager.FollowForPlacement());
    }

    [Fact]
    public async Task Placement_waits_while_a_library_rename_is_left_halfway()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Renamer.HasPending = true;

        Assert.Null(fixture.Manager.FollowForPlacement());
    }

    [Fact]
    public void Placement_waits_for_a_library_with_no_record_that_passes_as_the_own_library_of_a_server_missing_it()
    {
        // A crash between the creation and the save of the record (§10 row 384).
        var fixture = new Fixture();
        fixture.Server.AutomaticLibraries = true;
        fixture.Libraries.Add("Casa - Film", CollectionTypeOptions.movies, _movies);

        Assert.Null(fixture.Manager.FollowForPlacement());
    }

    [Fact]
    public async Task A_library_of_another_kind_or_folder_does_not_pass_as_the_own_library_of_a_server()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Serie"));
        fixture.Libraries.Add("Serie come film", CollectionTypeOptions.movies, _series);
        fixture.Libraries.Add("Serie e altro", CollectionTypeOptions.tvshows, _series, "/media/serie");

        Assert.NotNull(fixture.Manager.FollowForPlacement());
    }

    [Fact]
    public async Task A_record_the_follow_dropped_does_not_hold_placement_back_and_its_library_is_the_admin_s()
    {
        // The admin gave the library another folder: it leaves the registry and is treated as the admin's (§10 rows 383, 384).
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Find("Casa - Film").Locations = [_movies, "/media/film"];

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Equal(["Casa - Serie"], libraries.Automatic.Select(folder => folder.Name));
        Assert.Equal(["Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.True(fixture.Server.AutomaticLibraries);
    }

    [Fact]
    public async Task A_failed_creation_does_not_hold_placement_back()
    {
        var fixture = new Fixture();
        fixture.Libraries.CreateFailure = new IOException("disk");
        await fixture.Manager.Enable(_serverId).Job;
        Assert.True(fixture.Server.AutomaticLibraries);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);

        Assert.NotNull(fixture.Manager.FollowForPlacement());
    }

    [Fact]
    public async Task The_libraries_a_removed_server_left_count_as_automatic()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.RemoveServer();

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Equal(["Casa - Film", "Casa - Serie"], libraries.Automatic.Select(folder => folder.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_library_there_at_switch_on_that_passes_as_the_own_library_of_a_server_does_not_hold_placement_back()
    {
        // The admin's library on the server folder, with the automatic one gone: placement treats it as the admin's (§10 row 393).
        var fixture = new Fixture();
        fixture.Libraries.Add("Film", CollectionTypeOptions.movies, _movies);
        await fixture.Manager.Enable(_serverId).Job;
        Assert.Contains(fixture.Libraries.IdOf("Film"), fixture.Server.PreexistingLibraryIds);
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find("Casa - Film"));

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Equal(["Casa - Serie"], libraries.Automatic.Select(folder => folder.Name));
        Assert.Equal(["Casa - Film"], fixture.Server.ReleasedAutomaticLibraries);
    }

    [Fact]
    public async Task A_library_left_to_the_admin_that_passes_again_does_not_hold_placement_back()
    {
        // Its type changed and came back: it is no longer in the registry and stays the admin's (§10 rows 383, 393).
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Find("Casa - Film").CollectionType = CollectionTypeOptions.tvshows;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        Assert.Equal(["Casa - Film"], fixture.Server.ReleasedAutomaticLibraries);
        fixture.Libraries.Find("Casa - Film").CollectionType = CollectionTypeOptions.movies;

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Equal(["Casa - Serie"], libraries.Automatic.Select(folder => folder.Name));
    }
}
