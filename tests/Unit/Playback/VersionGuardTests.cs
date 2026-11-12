using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Security;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using LinkedChildType = MediaBrowser.Controller.Entities.LinkedChildType;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// Filtro sulle richieste (Q54, D8): a local copy linked to stub B as a version of one title.
public class VersionGuardTests
{
    private static readonly Guid _stubItem = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid _localItem = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly CoordinatorFixture _fixture = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private readonly Mock<ITranscodeManager> _transcodes = new();
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly StreamSlots _peerSlots = new(TimeProvider.System, new InMemoryConfigurationStore());
    private readonly InMemoryConfigurationStore _store = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Movie _stub;
    private readonly Movie _local;

    public VersionGuardTests()
    {
        var stub = PlaybackValues.Stub(PlaybackValues.Source()) with { Id = "b" };
        _store.Configuration.FederatedFolderPath = "/federata";
        _stub = Item(new Movie { Path = "/federata/server/Film/b/b.strm", ShortcutPath = PlaybackRoutes.StrmUrl(8096, null, "b", "signature") }, _stubItem);
        _local = Item(new Movie { Path = "/media/privata/film.mkv" }, _localItem);
        _library.Setup(library => library.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([new Folder()]);
        _stubs.Setup(stubs => stubs.Find("b")).Returns(stub);
        Linked(new VersionGroup { Id = "g", IdentityKey = "key", StubIds = ["b"], LocalItemIds = [_localItem] });
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = _user, Token = PlaybackValues.LocalToken, DeviceId = "device" });
        Sees(_stubItem);
    }

    private static string N(Guid id) => id.ToString("N");

    private static LinkedChild Link(Guid id) => new() { ItemId = id, Type = LinkedChildType.LinkedAlternateVersion };

    private T Item<T>(T item, Guid id)
        where T : BaseItem
    {
        item.Id = id;
        _items[id] = item;
        return item;
    }

    // Jellyfin links the alternates to the primary, as MergeVersions or the linker leave them.
    private static void Merged(Video primary, params Video[] alternates)
    {
        primary.LinkedAlternateVersions = alternates.Select(alternate => Link(alternate.Id)).ToArray();
        foreach (var alternate in alternates)
        {
            alternate.SetPrimaryVersionId(primary.Id);
        }
    }

    private Movie LocalFilm() => Item(new Movie { Path = "/media/privata/" + Guid.NewGuid().ToString("N") + ".mkv" }, Guid.NewGuid());

    private void Linked(VersionGroup? group)
    {
        _stubs.Setup(stubs => stubs.FindGroup("b")).Returns(group);
        _stubs.Setup(stubs => stubs.FindGroupOfLocal(_localItem)).Returns(group);
    }

    private void Sees(Guid item)
    {
        _library.Setup(library => library.GetItemById<BaseItem>(item, _user)).Returns(_items.GetValueOrDefault(item) ?? new Movie { Id = item });
    }

    // Jellyfin shows an item with no library to everyone, as between the removal of a path and the scan.
    private void InNoLibrary(Guid item)
    {
        _library.Setup(library => library.GetCollectionFolders(It.Is<BaseItem>(found => found.Id == item))).Returns([]);
    }

    private void Admin()
    {
        _user.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
    }

    private void Anonymous()
    {
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(new AuthorizationInfo());
    }

    private StockRouteGuardFilter Guard()
    {
        return new StockRouteGuardFilter(
            new StubLookup(_library.Object, _stubs.Object, _store),
            _authorization.Object,
            _fixture.Coordinator,
            _fixture.Sessions,
            new PlaybackRefusal(new Mock<ISessionManager>().Object, NullLogger<PlaybackRefusal>.Instance),
            _transcodes.Object,
            _peerSlots);
    }

    private static ActionExecutingContext Context(string controller, string action, params (string Key, object? Value)[] arguments)
    {
        var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action };
        var values = new Dictionary<string, object?>();
        foreach (var (key, value) in arguments)
        {
            values[key] = value;
        }

        return new ActionExecutingContext(new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor), [], values!, new object());
    }

    private static async Task<bool> RunAsync(IAsyncActionFilter filter, ActionExecutingContext context)
    {
        var passed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            passed = true;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });
        return passed;
    }

    private static int? StatusOf(ActionExecutingContext context) => (context.Result as StatusCodeResult)?.StatusCode;

    // Every route that names the stub as its entity and the local copy as its version, as stock binds them.
    public static TheoryData<string, string, string, string, bool> RoutesThatNameAVersion => new()
    {
        { "Videos", "GetVideoStream", "itemId", "mediaSourceId", true },
        { "Videos", "HeadVideoStream", "itemId", "mediaSourceId", true },
        { "DynamicHls", "GetMasterHlsVideoPlaylist", "itemId", "mediaSourceId", true },
        { "DynamicHls", "GetVariantHlsVideoPlaylist", "itemId", "mediaSourceId", true },
        { "DynamicHls", "GetHlsVideoSegment", "itemId", "mediaSourceId", true },
        { "Subtitle", "GetSubtitle", "routeItemId", "routeMediaSourceId", true },
        { "Subtitle", "GetSubtitlePlaylist", "itemId", "mediaSourceId", true },
        { "VideoAttachments", "GetAttachment", "videoId", "mediaSourceId", true },
        { "Trickplay", "GetTrickplayHlsPlaylist", "itemId", "mediaSourceId", false },
        { "Trickplay", "GetTrickplayTileImage", "itemId", "mediaSourceId", false }
    };

    [Theory]
    [MemberData(nameof(RoutesThatNameAVersion))]
    public async Task A_local_version_the_user_cannot_see_is_not_found_through_a_stub_they_see(string controller, string action, string item, string version, bool asText)
    {
        var context = Context(controller, action, (item, _stubItem), (version, asText ? N(_localItem) : _localItem), ("playSessionId", "play"));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Theory]
    [MemberData(nameof(RoutesThatNameAVersion))]
    public async Task A_stub_they_see_as_the_version_of_a_local_copy_they_cannot_see_is_not_found(string controller, string action, string item, string version, bool asText)
    {
        var context = Context(controller, action, (item, _localItem), (version, asText ? N(_stubItem) : _stubItem), ("playSessionId", "play"));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Theory]
    [MemberData(nameof(RoutesThatNameAVersion))]
    public async Task A_user_who_sees_both_versions_goes_to_stock(string controller, string action, string item, string version, bool asText)
    {
        Sees(_localItem);

        Assert.True(await RunAsync(Guard(), Context(controller, action, (item, _stubItem), (version, asText ? N(_localItem) : _localItem), ("playSessionId", "play"))));
    }

    [Fact]
    public async Task A_hidden_local_version_in_the_legacy_params_is_not_found()
    {
        var context = Context("Videos", "GetVideoStream", ("itemId", _stubItem), ("mediaSourceId", N(_stubItem)), ("params", ";;" + N(_localItem)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task A_linked_local_copy_named_alone_wants_a_user_who_sees_it()
    {
        var context = Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task A_linked_local_copy_named_alone_serves_a_user_who_sees_it()
    {
        Sees(_localItem);

        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)))));
    }

    [Fact]
    public async Task A_linked_local_copy_is_refused_without_a_user()
    {
        Anonymous();
        var context = Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(context));
    }

    [Fact]
    public async Task A_recorded_play_session_names_its_stub_beside_a_linked_local_copy()
    {
        Sees(_localItem);
        _library.Setup(library => library.GetItemById<BaseItem>(_stubItem, _user)).Returns((BaseItem?)null);
        _fixture.Sessions.RecordLocal(_user.Id, "play", _stubItem);
        var context = Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)), ("playSessionId", "play"));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task A_title_with_no_linked_versions_is_untouched()
    {
        Linked(null);
        Anonymous();

        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)))));
    }

    [Fact]
    public async Task A_group_with_one_member_has_no_linked_versions()
    {
        Linked(new VersionGroup { Id = "g", IdentityKey = "key", StubIds = ["b"] });

        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", _stubItem), ("mediaSourceId", N(_localItem)))));
    }

    [Fact]
    public async Task A_versioned_title_still_checks_an_unrelated_id_it_names()
    {
        var other = Guid.NewGuid();
        var context = Context("Videos", "GetVideoStream", ("itemId", _stubItem), ("mediaSourceId", N(other)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    public static TheoryData<string> ReportsThatNameAVersion => new()
    {
        "ReportPlaybackStart",
        "ReportPlaybackProgress",
        "ReportPlaybackStopped",
        "OnPlaybackStart",
        "OnPlaybackProgress",
        "OnPlaybackStopped",
        "OnPlaybackStartLegacy",
        "OnPlaybackProgressLegacy",
        "OnPlaybackStoppedLegacy"
    };

    // The arguments of a Playstate action as stock binds them: a body for the Report actions, the route and query otherwise.
    private static (string Key, object? Value)[] ReportOf(string action, Guid item, Guid version) => action switch
    {
        "ReportPlaybackStart" => [("playbackStartInfo", new PlaybackStartInfo { ItemId = item, MediaSourceId = N(version), PlaySessionId = "play" })],
        "ReportPlaybackProgress" => [("playbackProgressInfo", new PlaybackProgressInfo { ItemId = item, MediaSourceId = N(version), PlaySessionId = "play" })],
        "ReportPlaybackStopped" => [("playbackStopInfo", new PlaybackStopInfo { ItemId = item, MediaSourceId = N(version), PlaySessionId = "play" })],
        _ => [("itemId", item), ("mediaSourceId", N(version)), ("playSessionId", "play")]
    };

    [Theory]
    [MemberData(nameof(ReportsThatNameAVersion))]
    public async Task A_playstate_report_on_a_hidden_version_is_answered_without_effect(string action)
    {
        _fixture.Sessions.RecordLocal(_user.Id, "play", _stubItem);
        var context = Context("Playstate", action, ReportOf(action, _stubItem, _localItem));

        Assert.False(await RunAsync(Guard(), context));
        Assert.IsType<NoContentResult>(context.Result);
        Assert.Equal(_stubItem, _fixture.Sessions.LocalItemOf("play"));
    }

    [Theory]
    [MemberData(nameof(ReportsThatNameAVersion))]
    public async Task A_playstate_report_on_a_version_the_user_sees_reaches_stock(string action)
    {
        Sees(_localItem);

        Assert.True(await RunAsync(Guard(), Context("Playstate", action, ReportOf(action, _stubItem, _localItem))));
    }

    [Theory]
    [MemberData(nameof(ReportsThatNameAVersion))]
    public async Task A_playstate_report_whose_item_is_a_hidden_version_is_answered_without_effect(string action)
    {
        var context = Context("Playstate", action, ReportOf(action, _localItem, _stubItem));

        Assert.False(await RunAsync(Guard(), context));
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Fact]
    public async Task A_playstate_report_on_a_title_with_no_linked_versions_reaches_stock()
    {
        Linked(null);
        var report = new PlaybackProgressInfo { ItemId = _stubItem, MediaSourceId = N(_localItem) };

        Assert.True(await RunAsync(Guard(), Context("Playstate", "ReportPlaybackProgress", ("playbackProgressInfo", report))));
    }

    // Q168: the group left the registry, while Jellyfin keeps the link until the nightly run detaches it (Q144).
    [Theory]
    [MemberData(nameof(RoutesThatNameAVersion))]
    public async Task A_hidden_local_version_still_linked_after_its_group_left_the_registry_is_not_found(string controller, string action, string item, string version, bool asText)
    {
        Linked(null);
        Merged(_local, _stub);
        var context = Context(controller, action, (item, _stubItem), (version, asText ? N(_localItem) : _localItem), ("playSessionId", "play"));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task A_local_copy_still_linked_after_its_group_left_wants_a_user_who_sees_it()
    {
        Linked(null);
        Merged(_local, _stub);
        var hidden = Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)));
        Anonymous();
        var anonymous = Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)));

        Assert.False(await RunAsync(Guard(), anonymous));
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(anonymous));
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = _user, Token = PlaybackValues.LocalToken, DeviceId = "device" });
        Assert.False(await RunAsync(Guard(), hidden));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(hidden));
    }

    [Fact]
    public async Task A_user_who_sees_both_still_linked_versions_goes_to_stock()
    {
        Linked(null);
        Merged(_local, _stub);
        Sees(_localItem);

        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", _stubItem), ("mediaSourceId", N(_localItem)))));
    }

    [Fact]
    public async Task A_playstate_report_on_a_hidden_version_still_linked_is_answered_without_effect()
    {
        Linked(null);
        Merged(_local, _stub);
        var context = Context("Playstate", "ReportPlaybackProgress", ("playbackProgressInfo", new PlaybackProgressInfo { ItemId = _stubItem, MediaSourceId = N(_localItem) }));

        Assert.False(await RunAsync(Guard(), context));
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Fact]
    public async Task A_stub_out_of_the_registry_still_linked_under_the_federated_folder_counts()
    {
        Linked(null);
        var gone = Item(new Movie { Path = "/federata/server/Film/gone/gone.strm" }, Guid.NewGuid());
        Merged(_local, gone);
        var context = Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    // Q169: the film the admin merged the local copy into, which the stubs follow (Q162), is not in the group.
    [Fact]
    public async Task The_film_the_admin_merged_the_local_copy_into_named_alone_wants_a_user_who_sees_it()
    {
        var film = LocalFilm();
        Merged(film, _local, _stub);
        var context = Context("Videos", "GetVideoStream", ("itemId", film.Id), ("mediaSourceId", N(film.Id)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task Another_local_version_of_that_film_named_alone_wants_a_user_who_sees_it()
    {
        var film = LocalFilm();
        var other = LocalFilm();
        Merged(film, _local, other, _stub);
        var context = Context("Videos", "GetVideoStream", ("itemId", other.Id), ("mediaSourceId", N(other.Id)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task The_owner_of_a_file_version_in_the_group_named_alone_wants_a_user_who_sees_it()
    {
        var owner = LocalFilm();
        _local.OwnerId = owner.Id;
        _local.SetPrimaryVersionId(owner.Id);
        owner.LinkedAlternateVersions = [Link(_stubItem)];
        _stub.SetPrimaryVersionId(owner.Id);
        var context = Context("Videos", "GetVideoStream", ("itemId", owner.Id), ("mediaSourceId", N(owner.Id)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    // Q169: a file version of a film that is itself an alternate of the Q162 primary holding the stubs, two levels up.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_version_of_an_alternate_of_the_primary_with_the_stubs_named_alone_wants_a_user(bool alsoPrimaryVersion)
    {
        Linked(null);
        Anonymous();
        var primary = LocalFilm();
        var owner = LocalFilm();
        var file = LocalFilm();
        Merged(primary, owner, _stub);
        file.OwnerId = owner.Id;
        if (alsoPrimaryVersion)
        {
            file.SetPrimaryVersionId(owner.Id);
        }

        var context = Context("Videos", "GetVideoStream", ("itemId", file.Id), ("mediaSourceId", N(file.Id)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(context));
    }

    [Fact]
    public void An_empty_primary_or_link_id_is_never_looked_up()
    {
        Linked(null);
        _local.SetPrimaryVersionId(Guid.Empty);
        _local.LinkedAlternateVersions = [Link(Guid.Empty)];

        Assert.False(new StubLookup(_library.Object, _stubs.Object, _store).HasVersions(_localItem));
        _library.Verify(library => library.GetItemById(Guid.Empty), Times.Never);
    }

    [Fact]
    public async Task A_merge_of_the_admin_between_local_films_stays_stock()
    {
        Linked(null);
        Anonymous();
        var film = LocalFilm();
        Merged(film, _local);

        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", film.Id), ("mediaSourceId", N(_localItem)))));
        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", _localItem), ("mediaSourceId", N(_localItem)))));
    }

    // D17: Jellyfin shows a stub or a version with no library to everyone; the guard refuses it to non-admins.
    [Fact]
    public async Task A_stub_in_no_library_is_not_found_for_a_user_who_is_not_an_admin()
    {
        Linked(null);
        InNoLibrary(_stubItem);
        var context = Context("Videos", "GetVideoStream", ("itemId", _stubItem), ("mediaSourceId", N(_stubItem)));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task A_stub_in_no_library_serves_an_admin()
    {
        Linked(null);
        InNoLibrary(_stubItem);
        Admin();

        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", _stubItem), ("mediaSourceId", N(_stubItem)))));
    }

    [Theory]
    [MemberData(nameof(RoutesThatNameAVersion))]
    public async Task A_version_in_no_library_is_not_found_for_a_user_who_is_not_an_admin(string controller, string action, string item, string version, bool asText)
    {
        Sees(_localItem);
        InNoLibrary(_localItem);
        var context = Context(controller, action, (item, _stubItem), (version, asText ? N(_localItem) : _localItem), ("playSessionId", "play"));

        Assert.False(await RunAsync(Guard(), context));
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(context));
    }

    [Fact]
    public async Task A_version_in_no_library_serves_an_admin()
    {
        Sees(_localItem);
        InNoLibrary(_localItem);
        Admin();

        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", _stubItem), ("mediaSourceId", N(_localItem)))));
    }

    [Theory]
    [MemberData(nameof(ReportsThatNameAVersion))]
    public async Task A_playstate_report_on_a_version_in_no_library_is_answered_without_effect(string action)
    {
        Sees(_localItem);
        InNoLibrary(_localItem);
        var context = Context("Playstate", action, ReportOf(action, _stubItem, _localItem));

        Assert.False(await RunAsync(Guard(), context));
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Fact]
    public async Task A_film_in_no_library_with_no_linked_versions_stays_stock()
    {
        Linked(null);
        var film = LocalFilm();
        Sees(film.Id);
        InNoLibrary(film.Id);

        Assert.True(await RunAsync(Guard(), Context("Videos", "GetVideoStream", ("itemId", film.Id), ("mediaSourceId", N(film.Id)))));
    }
}
