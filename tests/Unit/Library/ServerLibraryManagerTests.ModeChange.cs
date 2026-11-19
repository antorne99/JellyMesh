using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
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
        Assert.Empty(toRemote.Deleted);
        Assert.Equal(["Film di casa"], toRemote.Released);
        Assert.Equal(["Casa - Serie"], toNone!.Deleted);
        Assert.Equal(["Film di casa"], toNone.Released);
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

    [Fact]
    public async Task A_change_to_none_with_no_library_left_forgets_the_libraries_asked_before_a_crash()
    {
        var fixture = OnTypeFolders();
        fixture.Server.LibraryMode = LibraryMode.None;
        fixture.Server.PendingLibraries.Add(new PendingLibraryConfig { Name = "Casa - Film", Kind = ServerLibraryKind.Movies });

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Server.PendingLibraries);
    }

    [Fact]
    public async Task A_check_forgets_the_libraries_asked_before_a_crash_of_a_server_in_none_mode()
    {
        var fixture = OnTypeFolders();
        fixture.Server.LibraryMode = LibraryMode.None;
        fixture.Server.PendingLibraries.Add(new PendingLibraryConfig { Name = "Casa - Film", Kind = ServerLibraryKind.Movies });

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Server.PendingLibraries);
    }

    [Fact]
    public async Task Users_with_access_to_an_old_library_get_the_new_ones_that_hold_its_titles_before_it_goes()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        var oldFilms = Guid.Parse(fixture.Libraries.Find("Casa - Film").ItemId);
        var removedAtCarry = -1;
        fixture.Renamer.OnCarry = () => removedAtCarry = fixture.Libraries.Removed.Count;

        await fixture.Manager.ApplyMode(_serverId).Job;

        var carried = Assert.Single(fixture.Renamer.Carried);
        Assert.Equal(
            new[] { "Casa - Film 4K", "Casa 1a2b3c4d - Film" }.Select(name => (name, (Guid?)Guid.Parse(fixture.Libraries.Find(name).ItemId))),
            carried.Select(carry => (carry.Name, carry.Id)).OrderBy(pair => pair.Name, StringComparer.Ordinal));
        Assert.All(carried, carry => Assert.Equal([oldFilms], Assert.Single(carry.Folders)));
        Assert.Equal(0, removedAtCarry);
    }

    [Fact]
    public async Task A_library_that_merges_two_old_ones_carries_each_folder_with_the_one_that_held_it()
    {
        var fixture = await ChangedFrom(LibraryMode.ByRemoteLibrary, LibraryMode.ByType);
        var children = Guid.Parse(fixture.Libraries.Find("Casa - Film").ItemId);
        var adults = Guid.Parse(fixture.Libraries.Find("Casa - Film 4K").ItemId);

        await fixture.Manager.ApplyMode(_serverId).Job;

        var carry = Assert.Single(Assert.Single(fixture.Renamer.Carried));
        Assert.Equal(("Casa 1a2b3c4d - Film", (Guid?)Guid.Parse(fixture.Libraries.Find("Casa 1a2b3c4d - Film").ItemId)), (carry.Name, carry.Id));
        Assert.Equal(2, carry.Folders.Count);
        Assert.Equal([children], carry.Folders[0]);
        Assert.Equal([adults], carry.Folders[1]);
    }

    [Fact]
    public async Task The_users_who_lose_titles_stay_on_the_server_config_after_the_change_and_a_restart()
    {
        var fixture = await ChangedFrom(LibraryMode.ByRemoteLibrary, LibraryMode.ByType);
        fixture.Renamer.Lost = [new AccessLoss("Casa 1a2b3c4d - Film", ["ristretto"])];

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal([("Casa 1a2b3c4d - Film", "ristretto")], fixture.Server.LostAccess.Select(lost => (lost.Library, lost.User)));
        var shown = Assert.Single(fixture.NewManager().GetState(_serverId).LostAccess);
        Assert.Equal("Casa 1a2b3c4d - Film", shown.Library);
        Assert.Equal(["ristretto"], shown.Users);
    }

    [Fact]
    public async Task A_new_mode_change_replaces_the_users_named_by_the_last_one()
    {
        var fixture = await ChangedFrom(LibraryMode.ByRemoteLibrary, LibraryMode.ByType);
        fixture.Server.LostAccess.Add(new LostAccessConfig { Library = "Casa - Film HD", User = "vecchio" });

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Server.LostAccess);
    }

    [Fact]
    public async Task A_change_resumed_after_a_restart_adds_to_the_users_already_named()
    {
        var fixture = await ChangedFrom(LibraryMode.ByRemoteLibrary, LibraryMode.ByType);
        fixture.Renamer.CarryFailure = new InvalidOperationException("database");
        await fixture.Manager.ApplyMode(_serverId).Job;
        fixture.Server.LostAccess.Add(new LostAccessConfig { Library = "Casa 1a2b3c4d - Film", User = "bambini" });
        fixture.Renamer.CarryFailure = null;
        fixture.Renamer.Lost = [new AccessLoss("Casa 1a2b3c4d - Film", ["bambini", "noadulti"])];

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(
            [("Casa 1a2b3c4d - Film", "bambini"), ("Casa 1a2b3c4d - Film", "noadulti")],
            fixture.Server.LostAccess.Select(lost => (lost.Library, lost.User)));
    }

    [Fact]
    public async Task A_library_the_new_mode_adopts_is_named_in_the_preview_and_given_to_nobody()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        var children = Guid.Parse(fixture.Libraries.Find("Casa - Film").ItemId);
        var adults = Guid.Parse(fixture.Libraries.Find("Casa - Film 4K").ItemId);
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        var adopted = Guid.Parse(fixture.Libraries.Find("Film di casa").ItemId);
        fixture.Renamer.Lost = [new AccessLoss("Film di casa", ["ristretto"])];

        var change = fixture.Manager.PreviewModeChange(_serverId, LibraryMode.ByType);
        fixture.Server.LibraryMode = LibraryMode.ByType;
        await fixture.Manager.ApplyMode(_serverId).Job;

        var previewed = Assert.Single(Assert.Single(fixture.Renamer.Previewed));
        Assert.Equal(("Film di casa", (Guid?)adopted, true), (previewed.Name, previewed.Id, previewed.Adopted));
        Assert.Equal<IReadOnlyList<Guid>>([[children], [adults]], previewed.Folders);
        Assert.Equal(fixture.Renamer.Lost, change!.LostAccess);
        var carried = Assert.Single(Assert.Single(fixture.Renamer.Carried));
        Assert.Equal(("Film di casa", (Guid?)adopted, true), (carried.Name, carried.Id, carried.Adopted));
        Assert.Equal<IReadOnlyList<Guid>>([[children], [adults]], carried.Folders);
        Assert.Equal([("Film di casa", "ristretto")], fixture.Server.LostAccess.Select(lost => (lost.Library, lost.User)));
    }

    [Fact]
    public async Task The_preview_names_the_merged_library_and_who_would_lose_titles_with_no_user_changed()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        var children = Guid.Parse(fixture.Libraries.Find("Casa - Film").ItemId);
        var adults = Guid.Parse(fixture.Libraries.Find("Casa - Film 4K").ItemId);
        fixture.Renamer.Lost = [new AccessLoss("Casa 1a2b3c4d - Film", ["ristretto"])];

        var change = fixture.Manager.PreviewModeChange(_serverId, LibraryMode.ByType);

        var carry = Assert.Single(Assert.Single(fixture.Renamer.Previewed));
        Assert.Equal(("Casa 1a2b3c4d - Film", (Guid?)null), (carry.Name, carry.Id));
        Assert.Equal<IReadOnlyList<Guid>>([[children], [adults]], carry.Folders);
        Assert.Equal(fixture.Renamer.Lost, change!.LostAccess);
        Assert.Empty(fixture.Renamer.Carried);
    }

    [Fact]
    public async Task Access_that_cannot_be_carried_keeps_the_old_libraries_until_a_later_check()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Renamer.CarryFailure = new InvalidOperationException("database");

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Libraries.Removed);
        fixture.Renamer.CarryFailure = null;
        await fixture.NewManager().ReconcileAsync(CancellationToken.None);
        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    // Records the creations, the blocks and the scans in one list, and gives the made libraries their ids at the first scan.
    private static void RecordBlockOrder(Fixture fixture)
    {
        fixture.Libraries.NoIdOnCreate = true;
        fixture.Libraries.OnCreate = () => fixture.Scans.Steps.Add("create");
        fixture.Renamer.OnBlock = () => fixture.Scans.Steps.Add("block");
        fixture.Scans.DuringScan = () =>
        {
            foreach (var folder in fixture.Libraries.Folders.Where(folder => folder.ItemId is null))
            {
                folder.ItemId = fixture.Libraries.IdOf(folder.Name);
            }
        };
        fixture.Scans.Steps.Clear();
    }

    [Fact]
    public async Task Who_blocks_an_old_library_has_each_new_one_blocked_before_it_is_made_and_again_before_the_scan()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        var oldFilms = Guid.Parse(fixture.Libraries.Find("Casa - Film").ItemId);
        RecordBlockOrder(fixture);

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(["wait", "block", "create", "block", "create", "block", "scan"], fixture.Scans.Steps.Take(7));
        var beforeMade = fixture.Renamer.Blocked.Take(2).Select(Assert.Single).ToList();
        Assert.Equal(fixture.Libraries.Created.TakeLast(2).Select(call => call.Name), beforeMade.Select(carry => carry.Name));
        Assert.All(beforeMade, carry => Assert.Null(carry.Id));
        Assert.All(beforeMade, carry => Assert.Equal([oldFilms], Assert.Single(carry.Folders)));
        Assert.Equal(
            [("Casa - Film 4K", (Guid?)null), ("Casa 1a2b3c4d - Film", null)],
            fixture.Renamer.Blocked[2].Select(carry => (carry.Name, carry.Id)).OrderBy(pair => pair.Name, StringComparer.Ordinal));
        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task A_change_resumed_by_a_check_blocks_the_new_libraries_before_it_scans_them()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Scans.ScanFailure = new InvalidOperationException("stopped");
        await fixture.Manager.ApplyMode(_serverId).Job;
        Assert.Empty(fixture.Libraries.Removed);
        fixture.Scans.ScanFailure = null;
        RecordBlockOrder(fixture);
        fixture.Renamer.Blocked.Clear();

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Equal(["block", "scan"], fixture.Scans.Steps.Where(step => step is "block" or "scan").Take(2));
        Assert.Equal(["Casa - Film 4K", "Casa 1a2b3c4d - Film"], fixture.Renamer.Blocked[0].Select(carry => carry.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task A_check_that_makes_the_libraries_a_change_could_not_make_blocks_them_before_it_scans_them()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Libraries.CreateFailure = new InvalidOperationException("refused");
        await fixture.Manager.ApplyMode(_serverId).Job;
        Assert.Empty(fixture.Libraries.Removed);
        fixture.Libraries.CreateFailure = null;
        RecordBlockOrder(fixture);
        fixture.Libraries.NoIdOnCreate = false;
        fixture.Renamer.Blocked.Clear();

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["block", "create", "block", "create", "block", "scan"], fixture.Scans.Steps.Where(step => step is "create" or "block" or "scan").Take(6));
        Assert.Equal(["Casa - Film 4K", "Casa 1a2b3c4d - Film"], fixture.Renamer.Blocked[2].Select(carry => carry.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task A_change_stalled_by_a_library_of_the_admin_blocks_the_libraries_already_made_before_their_scan()
    {
        var fixture = OnTypeFolders();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Libraries.Add("Film 4K miei", CollectionTypeOptions.movies, Absolute(_filmsBFolder), "/media/mie");
        fixture.Server.LibraryMode = LibraryMode.ByRemoteLibrary;
        RecordBlockOrder(fixture);

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(["wait", "block", "create", "block", "scan"], fixture.Scans.Steps.Take(5));
        Assert.Equal(["Casa 1a2b3c4d - Film"], fixture.Renamer.Blocked[0].Select(carry => carry.Name));
        Assert.Equal(["Casa 1a2b3c4d - Film"], fixture.Renamer.Blocked[1].Select(carry => carry.Name));
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Renamer.Carried);
    }

    [Fact]
    public async Task A_block_that_cannot_be_written_before_a_library_is_made_leaves_it_unmade_and_the_next_check_makes_it()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Libraries.Created.Clear();
        fixture.Scans.Steps.Clear();
        fixture.Renamer.BlockFailure = new InvalidOperationException("database");

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Libraries.Created);
        Assert.Empty(fixture.Server.PendingLibraries);
        Assert.DoesNotContain("scan", fixture.Scans.Steps);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Renamer.Carried);

        fixture.Renamer.BlockFailure = null;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, fixture.Libraries.Created.Count);
        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task A_block_that_cannot_be_written_after_the_libraries_are_made_stops_the_change_before_any_scan_and_the_next_check_finishes_it()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Libraries.Created.Clear();
        fixture.Scans.Steps.Clear();
        fixture.Renamer.OnBlock = () =>
        {
            if (fixture.Libraries.Created.Count == 2)
            {
                throw new InvalidOperationException("database");
            }
        };

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(2, fixture.Libraries.Created.Count);
        Assert.DoesNotContain("scan", fixture.Scans.Steps);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Renamer.Carried);

        fixture.Renamer.OnBlock = null;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task A_library_the_plugin_deletes_leaves_the_given_lists_before_it_goes_and_the_blocked_ones_after()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        var oldFilms = Guid.Parse(fixture.Libraries.Find("Casa - Film").ItemId);
        var removedAt = new List<int>();
        fixture.Renamer.OnRemove = () => removedAt.Add(fixture.Libraries.Removed.Count);

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(
            [("Casa - Film", (Guid?)oldFilms, PreferenceKind.EnabledFolders), ("Casa - Film", oldFilms, PreferenceKind.BlockedMediaFolders)],
            fixture.Renamer.Removed);
        Assert.Equal([0, 1], removedAt);
    }

    [Fact]
    public async Task A_released_library_of_the_admin_stays_in_the_users_lists()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        fixture.Server.LibraryMode = LibraryMode.None;

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(["Casa - Serie", "Casa - Serie"], fixture.Renamer.Removed.Select(removed => removed.Name));
    }

    [Fact]
    public async Task A_library_still_given_to_a_user_is_not_deleted_and_the_next_check_deletes_it()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Renamer.RemoveFailure = new InvalidOperationException("database");

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record.Name == "Casa - Film");

        fixture.Renamer.RemoveFailure = null;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Film"], fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task A_new_library_without_an_id_keeps_the_old_libraries_and_carries_nothing()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Scans.DuringScan = () =>
        {
            if (fixture.Libraries.Folders.FirstOrDefault(folder => folder.Name == "Casa - Film 4K") is { } made)
            {
                made.ItemId = null;
            }
        };

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Renamer.Carried);
    }

    [Fact]
    public async Task A_new_library_the_admin_gave_another_path_keeps_the_old_libraries_and_carries_nothing()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByRemoteLibrary);
        fixture.Scans.DuringScan = () =>
        {
            if (fixture.Libraries.Folders.FirstOrDefault(folder => folder.Name == "Casa - Film 4K") is { } made && made.Locations.Length == 1)
            {
                made.Locations = [.. made.Locations, "/media/mie"];
            }
        };

        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Empty(fixture.Libraries.Removed);
        Assert.Empty(fixture.Renamer.Carried);
    }

    [Fact]
    public async Task Adopted_libraries_give_their_access_to_the_new_ones_and_are_never_written()
    {
        var fixture = OnTypeFolders();
        fixture.Libraries.Add("Film di casa", CollectionTypeOptions.movies, FilmPaths);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;
        var adopted = Guid.Parse(fixture.Libraries.Find("Film di casa").ItemId);
        fixture.Server.LibraryMode = LibraryMode.ByRemoteLibrary;

        await fixture.Manager.ApplyMode(_serverId).Job;

        var carried = Assert.Single(fixture.Renamer.Carried);
        Assert.Equal(2, carried.Count);
        Assert.All(carried, carry => Assert.Equal([adopted], Assert.Single(carry.Folders)));
        Assert.DoesNotContain(carried, carry => carry.Id == adopted);
    }

    [Fact]
    public async Task A_queued_switch_on_keeps_a_mode_the_form_saved_meanwhile()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scans.Block = release.Task;
        var rename = fixture.Manager.RenameAsync(_serverId, CancellationToken.None);
        await fixture.Scans.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var queued = fixture.Manager.Enable(_serverId);
        fixture.Server.LibraryMode = LibraryMode.None;
        release.SetResult();
        await rename.WaitAsync(TimeSpan.FromSeconds(10));
        await queued.Job.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AutomaticLibrariesOutcome.Started, queued.Outcome);
        Assert.Equal(LibraryMode.None, fixture.Server.LibraryMode);
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
    }

    [Fact]
    public async Task The_preview_counts_the_type_folders_a_by_type_library_takes_as_the_change_does()
    {
        var fixture = OnTypeFolders();
        fixture.Server.ImportedLibraryIds.Remove(TestLibraries.FilmsB);
        await fixture.Manager.Enable(_serverId, LibraryMode.ByRemoteLibrary).Job;
        HoldsFilms(fixture, _filmsFolder, _filmsBFolder);

        var change = fixture.Manager.PreviewModeChange(_serverId, LibraryMode.ByType);
        fixture.Server.LibraryMode = LibraryMode.ByType;
        await fixture.Manager.ApplyMode(_serverId).Job;

        Assert.Equal(["Casa - Film"], change!.Deleted);
        Assert.Equal(change.Deleted, fixture.Libraries.Removed.Select(call => call.Name));
    }

    [Fact]
    public async Task While_the_layout_is_unknown_the_preview_deletes_nothing_as_the_change_does()
    {
        var fixture = await ChangedFrom(LibraryMode.ByType, LibraryMode.ByType);
        fixture.Stubs.Setup(stubs => stubs.UsesOlderLayout()).Returns((bool?)null);

        var change = fixture.Manager.PreviewModeChange(_serverId, LibraryMode.ByRemoteLibrary);

        Assert.Empty(change!.Deleted);
        Assert.NotEmpty(fixture.Manager.PreviewModeChange(_serverId, LibraryMode.None)!.Deleted);
    }
}
