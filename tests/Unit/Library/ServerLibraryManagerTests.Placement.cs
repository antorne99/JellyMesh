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
    private static readonly Guid _otherId = Guid.Parse("1a2b3c4d-0000-4000-8000-000000000002");

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

    [Theory]
    [InlineData("before the follow reads the registries")]
    [InlineData("between the read and the save of a follow")]
    [InlineData("after the save of a follow")]
    public async Task A_sync_that_finds_placement_following_marks_no_check_skipped_so_no_rename_is_lost(string when)
    {
        // Placement holds the lock while it follows every registry, so the sync's check is not missed (§10 row 394).
        var fixture = new Fixture();
        var other = await TwoServersFollowedAsync(fixture);
        fixture.Libraries.RenameFromDashboard("Mare - Film", "Film del mare");
        var synced = false;
        void Sync()
        {
            Assert.True(fixture.Manager.ReconcileAsync(CancellationToken.None).IsCompletedSuccessfully);
            synced = true;
        }

        switch (when)
        {
            case "before the follow reads the registries":
                fixture.Libraries.OnRead = () =>
                {
                    fixture.Libraries.OnRead = null;
                    Sync();
                };
                break;
            case "between the read and the save of a follow":
                fixture.Hooks.BeforeUpdate = Sync;
                break;
            default:
                fixture.Hooks.AfterUpdate = Sync;
                break;
        }

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.True(synced);
        Assert.NotNull(libraries);
        Assert.Equal(["Film del mare", "Mare - Serie"], other.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.False(fixture.Server.FollowSkipped);
        Assert.False(other.FollowSkipped);

        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Film di casa", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
    }

    [Fact]
    public async Task A_sync_that_finds_placement_unable_to_follow_has_its_check_marked_skipped()
    {
        var fixture = new Fixture();
        var other = await TwoServersFollowedAsync(fixture);
        fixture.Libraries.RenameFromDashboard("Mare - Film", "Film del mare");
        fixture.Libraries.Find("Film del mare").ItemId = null;
        fixture.Libraries.OnRead = () =>
        {
            fixture.Libraries.OnRead = null;
            Assert.True(fixture.Manager.ReconcileAsync(CancellationToken.None).IsCompletedSuccessfully);
        };

        Assert.Null(fixture.Manager.FollowForPlacement());

        Assert.Null(fixture.Libraries.OnRead);
        Assert.True(fixture.Server.FollowSkipped);
        Assert.True(other.FollowSkipped);
    }

    [Fact]
    public async Task A_sync_while_a_job_holds_the_lock_after_a_placement_still_marks_its_check_skipped()
    {
        var fixture = new Fixture();
        var other = await TwoServersFollowedAsync(fixture);
        Assert.NotNull(fixture.Manager.FollowForPlacement());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var job = fixture.Manager.Disable(other.Id, true);
        await fixture.Scans.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await fixture.Manager.ReconcileAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(fixture.Server.FollowSkipped);
        release.SetResult();
        await job.Job.WaitAsync(TimeSpan.FromSeconds(10));
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

    [Fact]
    public async Task A_library_a_removed_server_left_renamed_in_the_dashboard_still_counts_as_automatic()
    {
        // The held registries are not followed, so the library is found by its folder (§10 row 395).
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.RemoveServer();
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Vecchio film");

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Equal(["Casa - Serie", "Vecchio film"], libraries.Automatic.Select(folder => folder.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_renamed_library_of_a_removed_server_does_not_hold_back_the_server_added_back_with_its_switch_on()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.RemoveServer();
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Vecchio film");
        fixture.AddServerBack("Casa").AutomaticLibraries = true;

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Contains(libraries.Automatic, folder => folder.Name == "Vecchio film");
    }

    [Fact]
    public async Task A_library_renamed_under_a_skipped_follow_does_not_hold_placement_back()
    {
        // The follow cannot adopt it and releases it under its current name, so it is the admin's (§10 rows 393, 401).
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Manager.MarkFollowSkipped();
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Equal(["Casa - Serie"], libraries.Automatic.Select(folder => folder.Name));
        Assert.Equal(["Film di casa"], fixture.Server.ReleasedAutomaticLibraries);
    }

    [Fact]
    public async Task A_library_renamed_under_a_skipped_follow_next_to_one_there_at_switch_on_does_not_hold_placement_back()
    {
        // The library there at switch-on is never seen by the registry, so the renamed one is still the only one (§10 row 401).
        var fixture = new Fixture();
        fixture.Libraries.Add("Film", CollectionTypeOptions.movies, _movies);
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Manager.MarkFollowSkipped();
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");

        var libraries = fixture.Manager.FollowForPlacement();

        Assert.NotNull(libraries);
        Assert.Equal(["Casa - Serie"], libraries.Automatic.Select(folder => folder.Name));
        Assert.Equal(["Film di casa"], fixture.Server.ReleasedAutomaticLibraries);
    }

    // Two servers with their switch on, each with its libraries, and a check that recorded them all.
    private static async Task<RemoteServerConfig> TwoServersFollowedAsync(Fixture fixture)
    {
        await fixture.Manager.Enable(_serverId).Job;
        var other = new RemoteServerConfig { Id = _otherId, Name = "Mare" };
        fixture.Store.Update(config => config.Servers.Add(other));
        await fixture.Manager.Enable(other.Id).Job;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        Assert.Equal(["Mare - Film", "Mare - Serie"], other.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.False(fixture.Server.FollowSkipped || other.FollowSkipped);
        return other;
    }

    // Runs a step once, just before or just after the next save.
    internal sealed class HookedStore(InMemoryConfigurationStore inner) : IPluginConfigurationStore
    {
        public event EventHandler? SettingsChanged
        {
            add => inner.SettingsChanged += value;
            remove => inner.SettingsChanged -= value;
        }

        public Action? BeforeUpdate { get; set; }

        public Action? AfterUpdate { get; set; }

        public T Read<T>(Func<PluginConfiguration, T> read) => inner.Read(read);

        public void Update(Action<PluginConfiguration> update)
        {
            var before = BeforeUpdate;
            BeforeUpdate = null;
            before?.Invoke();
            inner.Update(update);
            var after = AfterUpdate;
            AfterUpdate = null;
            after?.Invoke();
        }
    }
}
