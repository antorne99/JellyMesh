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

// The type folders of the remote libraries join the automatic libraries next to the server folder.
public sealed partial class ServerLibraryManagerTests
{
    private static readonly string _filmsFolder = FederatedPaths.TypeFolderOf(_serverId, TestLibraries.Films, TypeFolder.Movies);
    private static readonly string _filmsBFolder = FederatedPaths.TypeFolderOf(_serverId, TestLibraries.FilmsB, TypeFolder.Movies);

    private static string Absolute(string relative) => $"{Root}/{relative.Replace('\\', '/')}";

    private static void HoldsFilms(Fixture fixture, params string[] folders)
        => fixture.Stubs.Setup(stubs => stubs.TypeFoldersOf(_serverId, TypeFolder.Movies)).Returns(folders);

    [Fact]
    public async Task Switching_on_creates_the_film_library_on_the_server_folder_and_the_type_folders_that_hold_films()
    {
        var fixture = new Fixture();
        HoldsFilms(fixture, _filmsFolder);
        fixture.Stubs.Setup(stubs => stubs.TypeFoldersOf(_serverId, TypeFolder.Series)).Returns([]);

        await fixture.Manager.Enable(_serverId).Job;

        var film = fixture.Libraries.Created.Single(call => call.Type == CollectionTypeOptions.movies);
        var series = fixture.Libraries.Created.Single(call => call.Type == CollectionTypeOptions.tvshows);
        Assert.Equal([_movies, Absolute(_filmsFolder)], film.Options.PathInfos.Select(info => info.Path));
        Assert.Equal([_series], series.Options.PathInfos.Select(info => info.Path));
        Assert.Equal(2, fixture.Server.AutomaticLibraryRegistry.Count(record => record.ItemId.Length > 0));
    }

    [Fact]
    public async Task A_sync_that_brings_another_remote_library_adds_its_folder_and_keeps_the_library()
    {
        var fixture = new Fixture();
        HoldsFilms(fixture, _filmsFolder);
        await fixture.Manager.Enable(_serverId).Job;
        HoldsFilms(fixture, _filmsFolder, _filmsBFolder);
        fixture.Scans.Steps.Clear();

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([("Casa - Film", Absolute(_filmsBFolder))], fixture.Libraries.AddedPaths);
        Assert.Equal([_movies, Absolute(_filmsFolder), Absolute(_filmsBFolder)], fixture.Libraries.Find("Casa - Film").Locations);
        Assert.Equal(["Casa - Film", "Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Empty(fixture.Server.ReleasedAutomaticLibraries);
        Assert.Equal(["scan"], fixture.Scans.Steps);

        await fixture.NewManager().ReconcileAsync(CancellationToken.None);

        Assert.Single(fixture.Libraries.AddedPaths);
        Assert.Equal(2, fixture.Server.AutomaticLibraryRegistry.Count);
    }

    [Fact]
    public async Task While_the_registry_cannot_tell_where_the_titles_are_no_folder_is_added()
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Scans.Steps.Clear();

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Empty(fixture.Libraries.AddedPaths);
        Assert.Equal([_movies], fixture.Libraries.Find("Casa - Film").Locations);
        Assert.Empty(fixture.Scans.Steps);
    }

    [Fact]
    public async Task On_the_server_folder_layout_the_libraries_keep_their_one_path()
    {
        var fixture = new Fixture();
        HoldsFilms(fixture);
        fixture.Stubs.Setup(stubs => stubs.TypeFoldersOf(_serverId, TypeFolder.Series)).Returns([]);
        await fixture.Manager.Enable(_serverId).Job;

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal([_movies], fixture.Libraries.Find("Casa - Film").Locations);
        Assert.Equal([_series], fixture.Libraries.Find("Casa - Serie").Locations);
        Assert.Empty(fixture.Libraries.AddedPaths);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("type")]
    [InlineData("server")]
    public async Task A_library_with_a_path_that_is_not_a_type_folder_of_its_server_is_released(string extra)
    {
        var fixture = new Fixture();
        await fixture.Manager.Enable(_serverId).Job;
        var library = fixture.Libraries.Find("Casa - Film");
        library.Locations =
        [
            .. library.Locations,
            extra switch
            {
                "other" => "/elsewhere/Film",
                "type" => Absolute(FederatedPaths.TypeFolderOf(_serverId, TestLibraries.Films, TypeFolder.Series)),
                _ => Absolute(FederatedPaths.TypeFolderOf(Guid.NewGuid(), TestLibraries.Films, TypeFolder.Movies))
            }
        ];

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(["Casa - Serie"], fixture.Server.AutomaticLibraryRegistry.Select(record => record.Name));
        Assert.Empty(fixture.Libraries.AddedPaths);
    }

    [Fact]
    public async Task A_library_with_type_folders_left_by_a_new_federated_folder_is_released_by_name()
    {
        var fixture = new Fixture();
        HoldsFilms(fixture, _filmsFolder);
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Store.Configuration.FederatedFolderPath = "/nuova";

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.DoesNotContain(fixture.Server.AutomaticLibraryRegistry, record => record.Kind == ServerLibraryKind.Movies);
        Assert.Contains("Casa - Film", fixture.Server.ReleasedAutomaticLibraries);
        Assert.Empty(fixture.Libraries.AddedPaths);
    }

    [Fact]
    public async Task A_library_holding_only_type_folders_of_its_server_stays_its_own_and_takes_back_the_folder_its_titles_use()
    {
        var fixture = new Fixture();
        HoldsFilms(fixture, _filmsFolder);
        await fixture.Manager.Enable(_serverId).Job;
        fixture.Libraries.Find("Casa - Film").Locations = [Absolute(_filmsFolder)];

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Contains(fixture.Server.AutomaticLibraryRegistry, record => record.Kind == ServerLibraryKind.Movies);
        Assert.Equal([("Casa - Film", _movies)], fixture.Libraries.AddedPaths);
    }
}
