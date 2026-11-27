using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Moq;
using Xunit;
using LinkedChildType = MediaBrowser.Controller.Entities.LinkedChildType;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// A .strm of the federated folder that the registry does not hold is refused to everyone and never reaches stock, which
// would answer with its signed URL.
public partial class PlaybackFiltersTests
{
    private const string StubPath = "/federata/S/Film/A/A.strm";
    private const string ForeignUrl = "https://example.invalid/film.mkv";
    private static readonly Guid _unknownId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly InMemoryConfigurationStore _settings = new();

    private static string OurUrl(string stubId) => PlaybackRoutes.StrmUrl(8096, null, stubId, "signature");

    // Adds an item in a library that its user may or may not see.
    private Movie ItemOf(Guid id, string? path, string? url, bool visible = true)
    {
        var movie = new Movie { Id = id, Path = path, ShortcutPath = url };
        _library.Setup(library => library.GetItemById(id)).Returns(movie);
        if (visible)
        {
            _library.Setup(library => library.GetItemById<BaseItem>(id, _user)).Returns(movie);
        }

        return movie;
    }

    private Movie UnknownStub(bool visible = true)
    {
        _settings.Configuration.FederatedFolderPath = "/federata";
        return ItemOf(_unknownId, StubPath, OurUrl("gone"), visible);
    }

    private void IsAdministrator() => _user.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));

    private static ActionExecutingContext InfoOf(Guid item, Guid? version = null, string action = "GetPostedPlaybackInfo")
        => version is { } named
            ? Context("MediaInfo", action, ("itemId", item), ("mediaSourceId", named.ToString("N")))
            : Context("MediaInfo", action, ("itemId", item));

    private async Task AssertInfoAsync(int status, ActionExecutingContext context)
    {
        Assert.False(await RunAsync(InfoFilter(_settings), context));
        Assert.Equal(status, StatusOf(context));
        Assert.Empty(_fixture.Client.Requests);
    }

    private async Task AssertGuardAsync(int status, ActionExecutingContext context)
    {
        Assert.False(await RunAsync(GuardFilter(store: _settings), context));
        Assert.Equal(status, StatusOf(context));
    }

    [Theory]
    [InlineData("GetPlaybackInfo", false, false)]
    [InlineData("GetPostedPlaybackInfo", false, false)]
    [InlineData("GetPlaybackInfo", true, false)]
    [InlineData("GetPostedPlaybackInfo", true, true)]
    [InlineData("GetPostedPlaybackInfo", false, true)]
    public async Task PlaybackInfo_of_an_unknown_stub_is_refused(string action, bool administrator, bool damaged)
    {
        UnknownStub();
        if (administrator)
        {
            IsAdministrator();
        }

        if (damaged)
        {
            _fixture.Damage();
        }

        await AssertInfoAsync(StatusCodes.Status503ServiceUnavailable, InfoOf(_unknownId, action: action));
        PlaybackLogAssert.NoRemoteValue(_infoLog.Lines);
    }

    [Fact]
    public async Task PlaybackInfo_of_an_unknown_stub_wants_a_user_who_reaches_it()
    {
        UnknownStub(visible: false);
        await AssertInfoAsync(StatusCodes.Status404NotFound, InfoOf(_unknownId));

        UnknownStub();
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        await AssertInfoAsync(StatusCodes.Status404NotFound, InfoOf(_unknownId));

        Anonymous();
        await AssertInfoAsync(StatusCodes.Status401Unauthorized, InfoOf(_unknownId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackInfo_that_names_an_unknown_stub_as_the_version_is_refused(bool ofLocal)
    {
        UnknownStub();
        var route = ItemOf(ofLocal ? _localId : _itemId, ofLocal ? "/media/film/A.mkv" : "/federata/T/Film/A/A.strm", ofLocal ? null : OurUrl("stub"));
        route.LinkedAlternateVersions = [new LinkedChild { ItemId = _unknownId, Type = LinkedChildType.LinkedAlternateVersion }];

        await AssertInfoAsync(StatusCodes.Status503ServiceUnavailable, InfoOf(route.Id, _unknownId));
    }

    [Fact]
    public async Task PlaybackInfo_that_names_an_unknown_stub_of_another_title_is_not_found()
    {
        UnknownStub();
        ItemOf(_localId, "/media/film/A.mkv", null);

        await AssertInfoAsync(StatusCodes.Status404NotFound, InfoOf(_localId, _unknownId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_local_version_of_an_unknown_stub_plays_from_the_local_item(bool named)
    {
        var stub = UnknownStub();
        ItemOf(_localId, "/media/film/A.mkv", null);
        stub.LinkedAlternateVersions = [new LinkedChild { ItemId = _localId, Type = LinkedChildType.LinkedAlternateVersion }];
        var context = InfoOf(_unknownId, named ? _localId : null);
        var executed = await StockAsync(context, LocalFile(directPlay: true), Signed(_unknownId));

        Assert.Null(context.Result);
        Assert.Equal(_localId, context.ActionArguments["itemId"]);
        Assert.Equal(_localId.ToString("N"), context.ActionArguments["mediaSourceId"]);
        var source = Assert.Single(SourcesOf(executed));
        Assert.Equal(_localId.ToString("N"), source.Id);
        Assert.Null(source.Path);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.StartsWith("/Videos/" + _localId.ToString("N") + "/stream.", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Equal(_localId, _fixture.Sessions.LocalItemOf("local-session"));
    }

    [Fact]
    public async Task A_local_version_of_an_unknown_stub_that_stock_does_not_answer_is_refused()
    {
        var stub = UnknownStub();
        ItemOf(_localId, "/media/film/A.mkv", null);
        stub.LinkedAlternateVersions = [new LinkedChild { ItemId = _localId, Type = LinkedChildType.LinkedAlternateVersion }];

        var executed = await StockAsync(InfoOf(_unknownId));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(executed.Result).StatusCode);
    }

    [Fact]
    public async Task A_local_version_the_user_does_not_see_does_not_replace_an_unknown_stub()
    {
        var stub = UnknownStub();
        ItemOf(_localId, "/media/film/A.mkv", null, visible: false);
        stub.LinkedAlternateVersions = [new LinkedChild { ItemId = _localId, Type = LinkedChildType.LinkedAlternateVersion }];

        await AssertInfoAsync(StatusCodes.Status503ServiceUnavailable, InfoOf(_unknownId));
        await AssertInfoAsync(StatusCodes.Status404NotFound, InfoOf(_unknownId, _localId));
    }

    [Fact]
    public async Task A_local_item_never_answers_with_the_source_of_a_stub()
    {
        UnknownStub();
        var local = ItemOf(_localId, "/media/film/A.mkv", null);
        local.LinkedAlternateVersions = [new LinkedChild { ItemId = _unknownId, Type = LinkedChildType.LinkedAlternateVersion }];

        Assert.Empty(SourcesOf(await StockAsync(InfoOf(_localId), Signed(_unknownId))));
    }

    // Row 552: a local item linked to a stub of a former folder follows row 548 until the registry is read.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task A_local_item_linked_to_a_stub_of_a_former_folder_keeps_only_its_source_while_the_registry_is_unread(bool unread, int sources)
    {
        _settings.Configuration.FederatedFolderPath = "/federata";
        _stubs.Setup(stubs => stubs.IsUnread).Returns(unread);
        ItemOf(_unknownId, "/vecchia/S/Film/A/A.strm", OurUrl("gone"));
        var local = ItemOf(_localId, "/media/film/A.mkv", null);
        local.LinkedAlternateVersions = [new LinkedChild { ItemId = _unknownId, Type = LinkedChildType.LinkedAlternateVersion }];

        var kept = SourcesOf(await StockAsync(InfoOf(_localId), LocalFile(directPlay: true), Signed(_unknownId)));

        Assert.Equal(sources, kept.Length);
        Assert.Equal(_localId.ToString("N"), kept[0].Id);
    }

    [Fact]
    public async Task Two_local_items_linked_together_stay_stock_while_the_registry_is_unread()
    {
        _settings.Configuration.FederatedFolderPath = "/federata";
        _stubs.Setup(stubs => stubs.IsUnread).Returns(true);
        var other = ItemOf(Guid.NewGuid(), "/media/film/A 4K.mkv", null);
        var local = ItemOf(_localId, "/media/film/A.mkv", null);
        local.LinkedAlternateVersions = [new LinkedChild { ItemId = other.Id, Type = LinkedChildType.LinkedAlternateVersion }];
        var context = InfoOf(_localId);

        Assert.Equal(2, SourcesOf(await StockAsync(context, LocalFile(directPlay: true), new MediaSourceInfo { Id = other.Id.ToString("N") })).Length);
        Assert.False(context.ActionArguments.ContainsKey("mediaSourceId"));
    }

    private static MediaSourceInfo Signed(Guid item) => new() { Id = item.ToString("N"), Path = OurUrl("gone") };

    private static MediaSourceInfo[] SourcesOf(ActionExecutedContext executed)
        => [.. Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(executed.Result).Value).MediaSources];

    private async Task<ActionExecutedContext> StockAsync(ActionExecutingContext context, params MediaSourceInfo[] sources)
    {
        var executed = new ActionExecutedContext(context, [], new object())
        {
            Result = new OkObjectResult(new PlaybackInfoResponse { MediaSources = sources, PlaySessionId = "local-session" })
        };
        await InfoFilter(_settings).OnActionExecutionAsync(context, () => Task.FromResult(executed));
        return executed;
    }

    public static TheoryData<string, string, string> StreamRoutes() => new()
    {
        { "Videos", "GetVideoStream", "itemId" },
        { "Videos", "GetVideoStreamByContainer", "itemId" },
        { "Videos", "HeadVideoStream", "mediaSourceId" },
        { "Audio", "GetAudioStream", "itemId" },
        { "UniversalAudio", "GetUniversalAudioStream", "itemId" },
        { "DynamicHls", "GetMasterHlsVideoPlaylist", "itemId" },
        { "DynamicHls", "GetHlsVideoSegment", "mediaSourceId" },
        { "DynamicHls", "GetLiveHlsStream", "itemId" },
    };

    private ActionExecutingContext StreamOf(string controller, string action, string argument)
        => argument == "itemId"
            ? Context(controller, action, ("itemId", _unknownId), ("playSessionId", "new-play"))
            : Context(controller, action, ("itemId", LocalItem()), (argument, _unknownId.ToString("N")), ("playSessionId", "new-play"));

    [Theory]
    [MemberData(nameof(StreamRoutes))]
    public async Task A_stock_stream_of_an_unknown_stub_is_refused(string controller, string action, string argument)
    {
        UnknownStub();
        await AssertGuardAsync(StatusCodes.Status503ServiceUnavailable, StreamOf(controller, action, argument));

        IsAdministrator();
        await AssertGuardAsync(StatusCodes.Status503ServiceUnavailable, StreamOf(controller, action, argument));

        Anonymous();
        await AssertGuardAsync(StatusCodes.Status401Unauthorized, StreamOf(controller, action, argument));
    }

    [Theory]
    [MemberData(nameof(StreamRoutes))]
    public async Task A_stock_stream_of_an_unknown_stub_the_user_does_not_see_is_not_found(string controller, string action, string argument)
    {
        UnknownStub(visible: false);

        await AssertGuardAsync(StatusCodes.Status404NotFound, StreamOf(controller, action, argument));
    }

    [Fact]
    public async Task A_stock_stream_that_names_an_unknown_stub_in_the_legacy_params_is_refused()
    {
        UnknownStub();
        var context = Context("Videos", "GetVideoStream", ("itemId", LocalItem()), ("params", LegacyParams(2, _unknownId.ToString("N"))));

        await AssertGuardAsync(StatusCodes.Status503ServiceUnavailable, context);
    }

    [Theory]
    [InlineData("Subtitle", "GetSubtitle", "routeItemId")]
    [InlineData("VideoAttachments", "GetAttachment", "videoId")]
    [InlineData("Trickplay", "GetTrickplayHlsPlaylist", "itemId")]
    [InlineData("MediaSegments", "GetItemSegments", "itemId")]
    public async Task The_other_media_routes_of_an_unknown_stub_want_a_user_who_sees_it(string controller, string action, string argument)
    {
        UnknownStub();
        Assert.True(await RunAsync(GuardFilter(store: _settings), Context(controller, action, (argument, _unknownId))));

        UnknownStub(visible: false);
        _library.Setup(library => library.GetItemById<BaseItem>(_unknownId, _user)).Returns((BaseItem?)null);
        await AssertGuardAsync(StatusCodes.Status404NotFound, Context(controller, action, (argument, _unknownId)));

        Anonymous();
        await AssertGuardAsync(StatusCodes.Status401Unauthorized, Context(controller, action, (argument, _unknownId)));
    }

    [Theory]
    [InlineData("GetFile")]
    [InlineData("GetDownload")]
    public async Task A_file_read_of_an_unknown_stub_is_refused(string action)
    {
        UnknownStub();
        MayDownload(true);
        IsAdministrator();
        await AssertGuardAsync(StatusCodes.Status503ServiceUnavailable, Context("Library", action, ("itemId", _unknownId)));

        Anonymous();
        await AssertGuardAsync(StatusCodes.Status401Unauthorized, Context("Library", action, ("itemId", _unknownId)));
    }

    [Fact]
    public async Task The_local_transcode_of_a_stub_that_became_unknown_goes_on_for_its_user_only()
    {
        UnknownStub();
        _fixture.Sessions.RecordLocal(_user.Id, "local-play", _unknownId);
        _fixture.Sessions.RecordLocal(Guid.NewGuid(), "other-play", _unknownId);

        Assert.True(await RunAsync(GuardFilter(store: _settings), Context("DynamicHls", "GetHlsVideoSegment", ("itemId", _unknownId), ("playSessionId", "local-play"))));
        await AssertGuardAsync(StatusCodes.Status404NotFound, Context("DynamicHls", "GetHlsVideoSegment", ("itemId", _unknownId), ("playSessionId", "other-play")));
        await AssertGuardAsync(StatusCodes.Status503ServiceUnavailable, Context("DynamicHls", "GetHlsVideoSegment", ("itemId", _unknownId), ("playSessionId", "no-play")));
    }

    // The folder is null while the settings were never read: a suspended start.
    [Theory]
    [InlineData("/federata", "/altrove/A.strm", ForeignUrl, true)]
    [InlineData("/federata", "/altrove/A.strm", null, true)]
    [InlineData("/federata", "/federata/S/Film/A/A.mkv", null, true)]
    [InlineData("/federata", StubPath, null, false)]
    [InlineData("", "/altrove/A.strm", ForeignUrl, true)]
    [InlineData("", "/altrove/A.strm", null, true)]
    [InlineData(null, "/altrove/A.strm", ForeignUrl, true)]
    [InlineData(null, "/altrove/A.strm", null, false)]
    [InlineData(null, StubPath, OurUrlOfGone, false)]
    [InlineData(null, "/media/film/A.mkv", null, true)]
    public async Task Only_a_strm_of_the_federated_folder_is_refused(string? folder, string path, string? url, bool stock)
    {
        _settings.Configuration.FederatedFolderPath = folder ?? string.Empty;
        _settings.Unavailable = folder is null;
        ItemOf(_unknownId, path, url);
        IsAdministrator();

        Assert.Equal(stock, await RunAsync(InfoFilter(_settings), InfoOf(_unknownId)));
        Assert.Equal(stock, await RunAsync(GuardFilter(store: _settings), Context("Videos", "GetVideoStream", ("itemId", _unknownId))));
    }

    private const string OurUrlOfGone = "http://127.0.0.1:8096/JellyMesh/Play/gone?sig=signature";

    // Row 186: what is left of a former federated folder is stock's, once the registry says it does not hold it.
    [Theory]
    [InlineData("/federata", false, true)]
    [InlineData("/federata", true, false)]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    public async Task A_stub_of_a_former_folder_is_stock_only_while_the_registry_is_read(string folder, bool unread, bool stock)
    {
        _settings.Configuration.FederatedFolderPath = folder;
        _stubs.Setup(stubs => stubs.IsUnread).Returns(unread);
        ItemOf(_unknownId, "/vecchia/S/Film/A/A.strm", OurUrl("gone"));

        Assert.Equal(stock, await RunAsync(InfoFilter(_settings), InfoOf(_unknownId)));
        Assert.Equal(stock, await RunAsync(GuardFilter(store: _settings), Context("Videos", "GetVideoStream", ("itemId", _unknownId))));
    }

    // A .strm just written, which Jellyfin has not read or still knows by its former stub, is the stub of its path.
    [Theory]
    [InlineData(null)]
    [InlineData(OurUrlOfGone)]
    public async Task A_stub_the_registry_knows_by_its_path_plays(string? url)
    {
        _settings.Configuration.FederatedFolderPath = "/federata";
        ItemOf(_unknownId, StubPath, url);
        _stubs.Setup(stubs => stubs.FindByPath(StubPath)).Returns(_stub);
        var context = InfoOf(_unknownId);

        Assert.False(await RunAsync(InfoFilter(_settings), context));

        var response = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(context.Result).Value);
        Assert.StartsWith("/JellyMesh/Playback/", response.MediaSources[0].TranscodingUrl, StringComparison.Ordinal);
        Assert.True(await RunAsync(GuardFilter(store: _settings), Context("Videos", "GetVideoStream", ("itemId", _unknownId))));
    }
}
