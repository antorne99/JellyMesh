using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The shared libraries, one per type with the folders of every server (§10 row 442, D41-D44).
public sealed partial class ServerLibraryManagerTests
{
    private const string SystemId = "4f1c2d3e5a6b4c8d9e0f1a2b3c4d5e6f";
    private const string SharedFilm = "Tutti i server - Film";
    private const string SharedSeries = "Tutti i server - Serie";
    private static readonly string _otherMovies = $"{Root}/{_otherId:D}/Film";
    private static readonly string _otherSeries = $"{Root}/{_otherId:D}/Serie";

    // Two servers in the 0.5 layout, neither with libraries of its own.
    private static Fixture WithTwoServers()
    {
        var fixture = new Fixture();
        fixture.Store.Update(config => config.Servers.Add(Fixture.WithLibraries(new RemoteServerConfig { Id = _otherId, Name = "Mare" })));
        return fixture;
    }

    private static SharedLibrariesConfig Shared(Fixture fixture) => fixture.Store.Configuration.SharedLibraries;

    [Fact]
    public async Task Switching_on_creates_one_shared_library_per_type_with_the_folders_of_every_server_and_one_scan()
    {
        var fixture = WithTwoServers();

        var request = fixture.Manager.EnableShared();
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.Equal(
            [(SharedFilm, CollectionTypeOptions.movies, false), (SharedSeries, CollectionTypeOptions.tvshows, false)],
            fixture.Libraries.Created.Select(call => (call.Name, call.Type, call.Refresh)).OrderBy(call => call.Name));
        Assert.Equal([_movies, _otherMovies], fixture.Libraries.Created.Single(call => call.Name == SharedFilm).Options.PathInfos.Select(info => info.Path));
        Assert.Equal([_series, _otherSeries], fixture.Libraries.Created.Single(call => call.Name == SharedSeries).Options.PathInfos.Select(info => info.Path));
        Assert.True(Shared(fixture).Enabled);
        Assert.All(Shared(fixture).Libraries, record => Assert.Equal(fixture.Libraries.IdOf(record.Name), record.ItemId));
        Assert.Empty(Shared(fixture).PendingLibraries);
        Assert.Single(fixture.Scans.Steps, step => step == "scan");
        Assert.Empty(fixture.Server.AutomaticLibraryRegistry);
    }

    [Fact]
    public async Task On_type_folders_the_shared_library_takes_the_type_folders_of_each_imported_library()
    {
        var fixture = OnTypeFolders();

        await fixture.Manager.EnableShared().Job;

        Assert.Equal([Absolute(_filmsFolder), Absolute(_filmsBFolder)], fixture.Libraries.Created.Single(call => call.Name == SharedFilm).Options.PathInfos.Select(info => info.Path));
        Assert.Equal([Absolute(_showsFolder)], fixture.Libraries.Created.Single(call => call.Name == SharedSeries).Options.PathInfos.Select(info => info.Path));
    }

    [Fact]
    public async Task A_taken_name_gets_the_first_digits_of_the_local_server_id()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add(SharedFilm, CollectionTypeOptions.movies, "/altro");

        await fixture.Manager.EnableShared().Job;

        Assert.Contains(fixture.Libraries.Created, call => call.Name == "Tutti i server 4f1c2d3e - Film");
        Assert.Contains(Shared(fixture).Libraries, record => record.Name == "Tutti i server 4f1c2d3e - Film");
    }

    [Fact]
    public async Task With_both_names_taken_the_library_is_not_made_and_the_page_says_why()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add(SharedFilm, CollectionTypeOptions.movies, "/altro");
        fixture.Libraries.Add("Tutti i server 4f1c2d3e - Film", CollectionTypeOptions.movies, "/altro2");

        await fixture.Manager.EnableShared().Job;

        Assert.DoesNotContain(fixture.Libraries.Created, call => call.Type == CollectionTypeOptions.movies);
        Assert.Equal(AutomaticLibrariesError.NameTaken, fixture.Manager.GetSharedState().Error);
    }

    [Fact]
    public async Task Before_it_exists_the_shared_library_is_blocked_for_who_blocks_a_library_on_its_folders()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Mio", CollectionTypeOptions.movies, _movies);
        var createdWhenBlocked = -1;
        fixture.Renamer.OnBlock = () => createdWhenBlocked = fixture.Libraries.Created.Count(call => call.Name == SharedFilm);

        await fixture.Manager.EnableShared().Job;

        var carry = Assert.Single(fixture.Renamer.Blocked.SelectMany(carries => carries));
        Assert.Equal(SharedFilm, carry.Name);
        Assert.Null(carry.Id);
        Assert.Equal([[Guid.Parse(fixture.Libraries.IdOf("Mio"))], []], carry.Folders.Select(folder => folder.ToList()));
        Assert.Equal(0, createdWhenBlocked);
        Assert.Contains(fixture.Libraries.Created, call => call.Name == SharedFilm);
    }

    [Fact]
    public async Task A_library_on_its_folders_not_scanned_yet_is_named_by_the_id_of_its_name()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Mio", CollectionTypeOptions.movies, _movies);
        fixture.Libraries.Find("Mio").ItemId = null;

        await fixture.Manager.EnableShared().Job;

        Assert.Null(fixture.Libraries.Find("Mio").ItemId);
        var carry = Assert.Single(fixture.Renamer.Blocked.SelectMany(carries => carries));
        Assert.Equal([[Guid.Parse(fixture.Libraries.IdOf("Mio"))], []], carry.Folders.Select(folder => folder.ToList()));
        Assert.Contains(fixture.Libraries.Created, call => call.Name == SharedFilm);
    }

    [Fact]
    public async Task A_library_on_its_folders_with_no_id_to_tell_leaves_the_shared_library_unmade()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Mio", CollectionTypeOptions.movies, _movies);
        fixture.Libraries.Find("Mio").ItemId = null;
        fixture.Renamer.NoIdOfName = true;

        await fixture.Manager.EnableShared().Job;

        Assert.DoesNotContain(fixture.Libraries.Created, call => call.Name == SharedFilm);
        Assert.Contains(fixture.Libraries.Created, call => call.Name == SharedSeries);
        Assert.Equal(AutomaticLibrariesError.Failed, fixture.Manager.GetSharedState().Error);
    }

    [Fact]
    public async Task A_shared_library_the_server_stores_under_another_name_is_blocked_by_that_name_before_a_scan()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Mio", CollectionTypeOptions.movies, _movies);
        fixture.Libraries.RenameOnCreate = name => name + "1";
        var scansWhenBlocked = new List<int>();
        fixture.Renamer.OnBlock = () => scansWhenBlocked.Add(fixture.Scans.Steps.Count(step => step == "scan"));

        await fixture.Manager.EnableShared().Job;

        var made = SharedFilm + "1";
        var carry = Assert.Single(fixture.Renamer.Blocked.SelectMany(carries => carries), carry => carry.Name == made);
        Assert.Equal(Guid.Parse(fixture.Libraries.IdOf(made)), carry.Id);
        Assert.Equal([[Guid.Parse(fixture.Libraries.IdOf("Mio"))], []], carry.Folders.Select(folder => folder.ToList()));
        Assert.All(scansWhenBlocked, count => Assert.Equal(0, count));
        Assert.Contains(Shared(fixture).Libraries, record => record.Name == made);
    }

    [Fact]
    public async Task A_shared_library_stored_under_another_name_that_cannot_be_blocked_is_deleted_before_a_scan()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Mio", CollectionTypeOptions.movies, _movies);
        fixture.Libraries.RenameOnCreate = name => name + "1";
        fixture.Renamer.OnBlock = () =>
        {
            if (fixture.Libraries.Folders.Any(folder => folder.Name == SharedFilm + "1"))
            {
                fixture.Renamer.BlockFailure = new InvalidOperationException("boom");
            }
        };

        await fixture.Manager.EnableShared().Job;

        Assert.Equal([SharedFilm + "1"], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.DoesNotContain(Shared(fixture).Libraries, record => record.Kind == ServerLibraryKind.Movies);
        Assert.Empty(Shared(fixture).PendingLibraries);
        Assert.Equal(AutomaticLibrariesError.Failed, fixture.Manager.GetSharedState().Error);
    }

    [Fact]
    public async Task A_failed_block_leaves_the_shared_library_unmade()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Mio", CollectionTypeOptions.movies, _movies);
        fixture.Renamer.BlockFailure = new InvalidOperationException("boom");

        await fixture.Manager.EnableShared().Job;

        Assert.DoesNotContain(fixture.Libraries.Created, call => call.Name == SharedFilm);
        Assert.Equal(AutomaticLibrariesError.Failed, fixture.Manager.GetSharedState().Error);
    }

    [Fact]
    public async Task A_library_with_exactly_its_folders_and_type_is_adopted_with_no_block()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Tutti", CollectionTypeOptions.movies, _otherMovies, _movies);

        await fixture.Manager.EnableShared().Job;

        Assert.DoesNotContain(fixture.Libraries.Created, call => call.Type == CollectionTypeOptions.movies);
        var record = Shared(fixture).Libraries.Single(library => library.Kind == ServerLibraryKind.Movies);
        Assert.Equal(("Tutti", true, fixture.Libraries.IdOf("Tutti")), (record.Name, record.Adopted, record.ItemId));
        Assert.DoesNotContain(fixture.Renamer.Blocked.SelectMany(carries => carries), carry => carry.Name == "Tutti");
        Assert.DoesNotContain(fixture.Libraries.IdOf("Tutti"), Shared(fixture).PreexistingLibraryIds);
        Assert.True(Assert.Single(fixture.Manager.GetSharedState().Libraries, library => library.Kind == ServerLibraryKind.Movies).Adopted);
    }

    [Theory]
    [InlineData("parent", LibraryConflictReason.ParentPath)]
    [InlineData("type", LibraryConflictReason.OtherType)]
    [InlineData("superset", LibraryConflictReason.OtherPaths)]
    public async Task A_library_in_the_way_blocks_the_shared_library_and_the_page_sees_it(string shape, LibraryConflictReason reason)
    {
        var fixture = WithTwoServers();
        switch (shape)
        {
            case "parent":
                fixture.Libraries.Add("Tutto", CollectionTypeOptions.movies, Root);
                break;
            case "type":
                fixture.Libraries.Add("Tutto", CollectionTypeOptions.tvshows, _movies);
                break;
            default:
                fixture.Libraries.Add("Tutto", CollectionTypeOptions.movies, _movies, _otherMovies, "/altro");
                break;
        }

        await fixture.Manager.EnableShared().Job;

        Assert.DoesNotContain(fixture.Libraries.Created, call => call.Name == SharedFilm);
        var conflict = Assert.Single(fixture.Manager.GetSharedState().Conflicts, conflict => conflict.Kind == ServerLibraryKind.Movies);
        Assert.Equal(("Tutto", reason), (conflict.Library, conflict.Reason));
        Assert.Equal([_movies, _otherMovies], conflict.Paths);
    }

    [Fact]
    public async Task A_library_holding_only_some_of_its_folders_is_ignored()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Mio", CollectionTypeOptions.movies, _movies, "/altro");

        await fixture.Manager.EnableShared().Job;

        Assert.Contains(fixture.Libraries.Created, call => call.Name == SharedFilm);
        Assert.Empty(fixture.Manager.GetSharedState().Conflicts);
    }

    [Fact]
    public async Task With_no_servers_the_libraries_are_made_empty_and_a_server_added_later_enters_at_once()
    {
        var fixture = new Fixture();
        fixture.Store.Update(config => config.Servers.Clear());

        await fixture.Manager.EnableShared().Job;

        Assert.All(fixture.Libraries.Created, call => Assert.Empty(call.Options.PathInfos));
        Assert.Equal(2, Shared(fixture).Libraries.Count);

        fixture.AddServerBack("Casa");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([(SharedFilm, _movies), (SharedSeries, _series)], fixture.Libraries.AddedPaths.OrderBy(added => added.Name));
        Assert.Equal(2, fixture.Libraries.Created.Count);
        Assert.Equal(2, Shared(fixture).Libraries.Count);
    }

    [Fact]
    public async Task A_shared_library_renamed_in_the_dashboard_is_followed_and_not_made_again()
    {
        var fixture = WithTwoServers();
        await fixture.Manager.EnableShared().Job;
        fixture.Libraries.RenameFromDashboard(SharedFilm, "Film di tutti");

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        var record = Shared(fixture).Libraries.Single(library => library.Kind == ServerLibraryKind.Movies);
        Assert.Equal(("Film di tutti", fixture.Libraries.IdOf("Film di tutti"), false), (record.Name, record.ItemId, record.Adopted));
        Assert.Equal(2, fixture.Libraries.Created.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_library_the_admin_makes_after_deleting_a_shared_one_is_not_taken_for_it(bool withServerFolder)
    {
        var fixture = WithTwoServers();
        await fixture.Manager.EnableShared().Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find(SharedFilm));
        string[] kept = withServerFolder ? [_movies] : [];
        fixture.Libraries.Add("Bambini", CollectionTypeOptions.movies, kept);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.DoesNotContain(Shared(fixture).Libraries, record => record.Kind == ServerLibraryKind.Movies);
        Assert.Equal(kept, fixture.Libraries.Find("Bambini").Locations);
        Assert.Equal(2, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task An_empty_shared_library_renamed_in_the_dashboard_is_left_to_the_admin()
    {
        var fixture = new Fixture();
        fixture.Store.Update(config => config.Servers.Clear());
        await fixture.Manager.EnableShared().Job;
        fixture.Libraries.RenameFromDashboard(SharedFilm, "Vuota");

        await fixture.Manager.ReconcileAsync(CancellationToken.None);
        fixture.AddServerBack("Casa");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.DoesNotContain(Shared(fixture).Libraries, record => record.Kind == ServerLibraryKind.Movies);
        Assert.Empty(fixture.Libraries.Find("Vuota").Locations);
        Assert.Equal(2, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task A_shared_library_keeps_the_paths_it_was_last_seen_with()
    {
        var fixture = WithTwoServers();

        await fixture.Manager.EnableShared().Job;

        Assert.Equal([_movies, _otherMovies], Shared(fixture).Libraries.Single(record => record.Kind == ServerLibraryKind.Movies).Paths);

        fixture.Store.Update(config => config.Servers.Remove(config.Servers.Single(server => server.Id == _otherId)));
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([_movies], Shared(fixture).Libraries.Single(record => record.Kind == ServerLibraryKind.Movies).Paths);
    }

    [Fact]
    public async Task The_library_of_a_single_server_with_the_same_folders_stays_apart_from_the_shared_one()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        await fixture.Manager.EnableShared().Job;
        fixture.Libraries.RenameFromDashboard("Casa - Film", "Film di casa");
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([SharedFilm, SharedSeries], Shared(fixture).Libraries.Select(record => record.Name).Order(StringComparer.Ordinal));
        Assert.All(Shared(fixture).Libraries, record => Assert.False(record.Adopted));
        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record.Name == "Film di casa");
        Assert.Equal(4, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task A_shared_library_the_admin_deleted_is_made_again_only_by_a_new_switch_on()
    {
        var fixture = WithTwoServers();
        await fixture.Manager.EnableShared().Job;
        fixture.Libraries.Folders.Remove(fixture.Libraries.Find(SharedFilm));

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(2, fixture.Libraries.Created.Count);
        Assert.DoesNotContain(Shared(fixture).Libraries, record => record.Kind == ServerLibraryKind.Movies);

        await fixture.Manager.DisableShared(true).Job;
        await fixture.Manager.EnableShared().Job;

        Assert.Contains(fixture.Libraries.Folders, folder => folder.Name == SharedFilm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_folders_of_a_removed_server_leave_the_shared_library_once_its_titles_are_gone(bool byCleanup)
    {
        var fixture = WithTwoServers();
        await fixture.Manager.EnableShared().Job;
        fixture.Store.Update(config => config.Servers.Remove(config.Servers.Single(server => server.Id == _otherId)));
        fixture.Stubs.Setup(stubs => stubs.HasTitlesOf(_otherId)).Returns(true);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([_movies, _otherMovies], fixture.Libraries.Find(SharedFilm).Locations);

        fixture.Stubs.Setup(stubs => stubs.HasTitlesOf(_otherId)).Returns(false);
        if (byCleanup)
        {
            await fixture.Manager.DeleteRemovedServerLibraries();
        }
        else
        {
            await fixture.Manager.ReconcileAsync(CancellationToken.None);
        }

        Assert.Equal([_movies], fixture.Libraries.Find(SharedFilm).Locations);
        Assert.Equal([_series], fixture.Libraries.Find(SharedSeries).Locations);
    }

    [Fact]
    public async Task Switching_off_asks_to_confirm_then_deletes_the_made_libraries_and_releases_the_adopted_ones()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Tutti", CollectionTypeOptions.movies, _movies, _otherMovies);
        await fixture.Manager.EnableShared().Job;

        var unconfirmed = fixture.Manager.DisableShared(false);

        Assert.Equal(AutomaticLibrariesOutcome.ConfirmationRequired, unconfirmed.Outcome);
        Assert.Equal([SharedSeries], unconfirmed.ToDelete);
        Assert.Equal(["Tutti"], unconfirmed.ToRelease);
        Assert.True(Shared(fixture).Enabled);

        var confirmed = fixture.Manager.DisableShared(true);
        await confirmed.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, confirmed.Outcome);
        Assert.Equal([SharedSeries], fixture.Libraries.Removed.Select(call => call.Name));
        Assert.Empty(fixture.Libraries.Find("Tutti").Locations);
        Assert.False(Shared(fixture).Enabled);
        Assert.Empty(Shared(fixture).Libraries);
        Assert.Empty(Shared(fixture).CreatedLibraryKeys);
    }

    [Fact]
    public async Task Switching_off_with_only_adopted_libraries_asks_to_confirm_with_the_libraries_it_releases()
    {
        var fixture = WithTwoServers();
        fixture.Libraries.Add("Tutti", CollectionTypeOptions.movies, _movies, _otherMovies);
        fixture.Libraries.Add("Tutte", CollectionTypeOptions.tvshows, _series, _otherSeries);
        await fixture.Manager.EnableShared().Job;

        var unconfirmed = fixture.Manager.DisableShared(false);

        Assert.Equal(AutomaticLibrariesOutcome.ConfirmationRequired, unconfirmed.Outcome);
        Assert.Empty(unconfirmed.ToDelete);
        Assert.Equal(["Tutte", "Tutti"], unconfirmed.ToRelease.Order(StringComparer.Ordinal));
        Assert.True(Shared(fixture).Enabled);

        var request = fixture.Manager.DisableShared(true);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.False(Shared(fixture).Enabled);
    }

    [Fact]
    public async Task A_library_made_while_a_confirmed_switch_off_waits_for_a_check_keeps_the_option_on()
    {
        var fixture = WithTwoServers();
        await fixture.Manager.EnableShared().Job;
        fixture.Libraries.Add("Nuova", CollectionTypeOptions.movies);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        fixture.Libraries.OnRead = () =>
        {
            entered.Set();
            release.Wait();
        };
        var check = Task.Run(() => fixture.Manager.ReconcileAsync(CancellationToken.None));
        entered.Wait();
        fixture.Libraries.OnRead = null;

        var request = fixture.Manager.DisableShared(true);
        fixture.Store.Update(config => config.SharedLibraries.Libraries.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Nuova", ItemId = fixture.Libraries.IdOf("Nuova") }));
        release.Set();
        await check;
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.True(Shared(fixture).Enabled);
        Assert.Empty(fixture.Libraries.Removed);
        Assert.Equal(AutomaticLibrariesError.Unconfirmed, fixture.Manager.GetSharedState().Error);
    }

    [Fact]
    public async Task Switching_off_with_nothing_to_delete_or_release_needs_no_confirmation()
    {
        var fixture = WithTwoServers();
        await fixture.Manager.EnableShared().Job;
        fixture.Libraries.Folders.Clear();
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        var request = fixture.Manager.DisableShared(false);
        await request.Job;

        Assert.Equal(AutomaticLibrariesOutcome.Started, request.Outcome);
        Assert.False(Shared(fixture).Enabled);
    }

    [Fact]
    public void Switching_on_with_no_federated_folder_is_refused()
    {
        var fixture = new Fixture(root: string.Empty);

        Assert.Equal(AutomaticLibrariesOutcome.NoFederatedFolder, fixture.Manager.EnableShared().Outcome);
        Assert.False(Shared(fixture).Enabled);
    }

    [Fact]
    public async Task A_shared_library_made_before_a_crash_is_recorded_at_the_next_check()
    {
        var fixture = WithTwoServers();
        await fixture.Manager.EnableShared().Job;
        var lost = Shared(fixture).Libraries.Single(record => record.Kind == ServerLibraryKind.Movies);
        fixture.Store.Update(config =>
        {
            config.SharedLibraries.Libraries.Remove(config.SharedLibraries.Libraries.Single(record => record.Kind == ServerLibraryKind.Movies));
            config.SharedLibraries.CreatedLibraryKeys.Remove("Movies");
            var intent = new PendingLibraryConfig { Kind = ServerLibraryKind.Movies, Name = SharedFilm };
            intent.Paths.Add(_movies);
            intent.Paths.Add(_otherMovies);
            config.SharedLibraries.PendingLibraries.Add(intent);
        });

        Assert.Null(fixture.Manager.FollowForPlacement());

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        var record = Shared(fixture).Libraries.Single(library => library.Kind == ServerLibraryKind.Movies);
        Assert.Equal((lost.Name, lost.ItemId), (record.Name, record.ItemId));
        Assert.Empty(Shared(fixture).PendingLibraries);
        Assert.Equal(2, fixture.Libraries.Created.Count);
    }

    [Fact]
    public async Task Placement_counts_the_shared_libraries_as_the_plugin_libraries()
    {
        var fixture = WithTwoServers();
        await fixture.Manager.EnableShared().Job;

        var placement = fixture.Manager.FollowForPlacement();

        Assert.NotNull(placement);
        Assert.Contains(placement.Automatic, folder => folder.Name == SharedFilm);
        Assert.Contains(placement.Automatic, folder => folder.Name == SharedSeries);
    }

    [Fact]
    public async Task A_server_switched_on_later_does_not_take_the_shared_library_as_its_own()
    {
        var fixture = new Fixture();
        await fixture.Manager.EnableShared().Job;

        await fixture.Manager.Enable(_serverId, LibraryMode.ByType).Job;

        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name).Order(StringComparer.Ordinal));
        Assert.All(fixture.Server.AutomaticLibraryRegistry, record => Assert.False(record.Adopted));
        Assert.Equal(2, Shared(fixture).Libraries.Count);
    }
}
