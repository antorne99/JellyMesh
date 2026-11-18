using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// A change of library mode from the Edit form: the new libraries and their scan first, then the old ones go (§10 row 438).
public sealed partial class ServerLibraryManagerTests
{
    // A server on its type folders with the libraries of a mode, then the mode the Edit form saved.
    private static async Task<Fixture> ChangedFrom(LibraryMode from, LibraryMode to)
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId, from).Job;
        fixture.Server.LibraryMode = to;
        return fixture;
    }

    [Fact]
    public async Task From_type_to_remote_library_creates_and_scans_the_new_libraries_before_deleting_the_old_ones()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        var removedAtScan = new List<int>();
        fixture.Scans.DuringScan = () => removedAtScan.Add(fixture.Libraries.Removed.Count);

        var request = fixture.Manager.ApplyMode(_serverId);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.Equal(0, removedAtScan.First());
        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(
            [
                ("Casa - Film 4K", Absolute(_filmsBFolder)),
                ("Casa - Serie", Absolute(_showsFolder)),
                ("Casa 1a2b3c4d - Film", Absolute(_filmsFolder))
            ],
            fixture.Libraries.Folders.Select(folder => (folder.Name, Assert.Single(folder.Locations))).OrderBy(pair => pair.Name, StringComparer.Ordinal));
        Assert.Equal(
            [TestLibraries.Shows, TestLibraries.Films, TestLibraries.FilmsB],
            fixture.Server.AutomaticLibraryRegistry.Select(record => record.RemoteLibraryId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task From_remote_library_to_type_makes_the_type_libraries_then_deletes_those_of_the_remote_libraries()
    {
        var fixture = await ChangedFrom(LibraryMode.ByRemoteLibrary, LibraryMode.ByType);
        fixture.Libraries.Created.Clear();

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(["Casa 1a2b3c4d - Film"], fixture.Libraries.Created.Select(call => call.Name));
        Assert.Equal(["Casa - Film", "Casa - Film 4K"], fixture.Libraries.Removed.Select(call => call.Name).Order(StringComparer.Ordinal));
        Assert.Equal(FilmPaths, fixture.Libraries.Find("Casa 1a2b3c4d - Film").Locations);
        Assert.Equal([Absolute(_showsFolder)], fixture.Libraries.Find("Casa - Serie").Locations);
        Assert.All(fixture.Server.AutomaticLibraryRegistry, record => Assert.Empty(record.RemoteLibraryId));
    }

    [Fact]
    public async Task A_library_with_the_name_and_paths_the_new_mode_wants_stays_with_its_id()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        var id = fixture.Libraries.Find("Casa - Serie").ItemId;

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.DoesNotContain(fixture.Libraries.Removed, call => call.Name == "Casa - Serie");
        var kept = Assert.Single(fixture.Server.AutomaticLibraryRegistry, record => record.Name == "Casa - Serie");
        Assert.Equal((id, TestLibraries.Shows, false), (kept.ItemId, kept.RemoteLibraryId, kept.Adopted));
    }

    [Fact]
    public async Task To_none_deletes_only_the_plugin_libraries_and_releases_the_adopted_ones_with_their_other_paths()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        fixture.Libraries.Add("Altro", CollectionTypeOptions.movies, "/media/altro");
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        var mine = fixture.Libraries.Find("Film di casa");
        mine.Locations = [.. mine.Locations, "/media/mie"];
        fixture.Server.LibraryMode = LibraryMode.None;
        fixture.Libraries.Created.Clear();

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Equal(["Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(["/media/mie"], fixture.Libraries.Find("Film di casa").Locations);
        Assert.Equal(["/media/altro"], fixture.Libraries.Find("Altro").Locations);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
        Assert.Equal(LibraryMode.None, fixture.Server.LibraryMode);
        Assert.Equal(3, fixture.Manager.GetState(_serverId).ManualPaths.Count);
    }

    [Fact]
    public async Task An_adopted_library_with_exactly_the_paths_of_a_new_library_is_kept_adopted_instead_of_released()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.FilmsB);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, Absolute(_filmsFolder));
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Server.LibraryMode = LibraryMode.ByRemoteLibrary;

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal([Absolute(_filmsFolder)], fixture.Libraries.Find("Film di casa").Locations);
        var adopted = Assert.Single(fixture.Server.AutomaticLibraryRegistry, record => record.Adopted);
        Assert.Equal(("Film di casa", TestLibraries.Films), (adopted.Name, adopted.RemoteLibraryId));
        Assert.Empty(fixture.Libraries.RemovedPaths);
    }

    [Fact]
    public async Task A_new_library_that_cannot_be_made_leaves_the_old_ones_and_the_next_check_finishes_the_change()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Libraries.CreateFailure = new InvalidOperationException("refused");

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Contains(fixture.Libraries.Folders, folder => folder.Name == "Casa - Film");
        Assert.Equal(AutomaticLibrariesError.CreateFailed, fixture.Manager.GetState(_serverId).Error);

        fixture.Libraries.CreateFailure = null;
        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Equal(3, fixture.Server.AutomaticLibraryRegistry.Count(record => record.RemoteLibraryId.Length > 0));
    }

    [Fact]
    public async Task An_old_library_that_cannot_be_deleted_is_deleted_by_a_later_check_after_a_scan()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Libraries.RemoveFailure = new InvalidOperationException("refused");
        await fixture.Manager.ApplyMode(_serverId).Job;
        Assert.Equal(AutomaticLibrariesError.RemoveFailed, fixture.Manager.GetState(_serverId).Error);
        fixture.Libraries.RemoveFailure = null;
        fixture.Scans.Steps.Clear();

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Contains("scan", fixture.Scans.Steps);
        Assert.Empty(fixture.Libraries.Created.Skip(4));
    }

    [Fact]
    public async Task Back_to_the_first_mode_makes_its_libraries_again()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        await fixture.Manager.ApplyMode(_serverId).Job;
        fixture.Server.LibraryMode = LibraryMode.ByType;

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name).Order(StringComparer.Ordinal));
        Assert.Equal(FilmPaths, fixture.Libraries.Find("Casa - Film").Locations);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Folders.Select(folder => folder.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_change_stopped_before_its_job_is_finished_by_the_next_check()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.All(fixture.Server.AutomaticLibraryRegistry, record => Assert.NotEmpty(record.RemoteLibraryId));
    }

    [Fact]
    public async Task A_change_to_none_stopped_before_its_job_is_finished_by_the_next_check()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.None);

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name).Order(StringComparer.Ordinal));
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
    }

    [Fact]
    public async Task While_the_layout_is_unknown_the_old_libraries_stay()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns((bool?)null);

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(2, fixture.Server.AutomaticLibraryRegistry.Count);
    }

    [Fact]
    public async Task On_the_older_layout_per_remote_library_keeps_the_type_libraries()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Server.LibraryMode = LibraryMode.ByRemoteLibrary;

        await fixture.Manager.ApplyMode(_serverId).Job;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(2, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task The_preview_lists_the_libraries_deleted_and_released_but_not_those_kept_and_changes_nothing()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        var toRemote = fixture.Manager.PreviewModeChange(_serverId, LibraryMode.ByRemoteLibrary);
        var toNone = fixture.Manager.PreviewModeChange(_serverId, LibraryMode.None);

        Assert.Equal((LibraryMode.ByType, LibraryMode.ByRemoteLibrary), (toRemote!.From, toRemote.To));
        Assert.Empty(toRemote.DeletedLibraries);
        Assert.Equal(["Film di casa"], toRemote.ReleasedLibraries);
        Assert.Equal(["Casa - Serie"], toNone!.DeletedLibraries);
        Assert.Equal(["Film di casa"], toNone.ReleasedLibraries);
        Assert.Null(fixture.Manager.PreviewModeChange(_serverId, LibraryMode.ByType));
        Assert.Equal(LibraryMode.ByType, fixture.Server.LibraryMode);
        Assert.Empty(fixture.Libraries.Removed);
    }

    [Fact]
    public async Task A_library_on_the_same_paths_with_another_name_is_made_new_then_the_old_one_deleted()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.FilmsB);
        foreach (var library in fixture.Server.RemoteLibraries)
        {
            library.Name = library.Id == TestLibraries.Shows ? "Telefilm" : library.Name + " HD";
        }

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Server.LibraryMode = LibraryMode.ByRemoteLibrary;
        fixture.Libraries.Created.Clear();

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(["Casa - Film HD", "Casa - Telefilm"], fixture.Libraries.Created.Select(call => call.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Libraries.Removed.Select(call => call.Name).Order(StringComparer.Ordinal));
        Assert.All(fixture.Server.AutomaticLibraryRegistry, record => Assert.False(record.Adopted));
        Assert.Empty(fixture.Manager.GetState(_serverId).Conflicts);
        Assert.Equal([Absolute(_showsFolder)], fixture.Libraries.Find("Casa - Telefilm").Locations);
    }

    [Fact]
    public async Task A_change_to_none_forgets_the_libraries_asked_before_a_crash()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.None);
        fixture.Server.PendingLibraries.Add(new PendingLibraryConfig { Name = "Casa - Altro", Kind = ServerLibraryKind.Movies });

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Server.PendingLibraries);
    }

    [Fact]
    public async Task After_the_old_libraries_go_a_scan_brings_back_the_titles_they_shared_with_the_new_ones()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        var removedAtScan = new List<int>();
        fixture.Scans.DuringScan = () => removedAtScan.Add(fixture.Libraries.Removed.Count);

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal([0, 1], removedAtScan);
    }

    [Fact]
    public async Task To_none_needs_no_scan()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.None);
        fixture.Scans.Steps.Clear();

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.DoesNotContain("scan", fixture.Scans.Steps);
    }
}
