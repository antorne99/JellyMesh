using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class PeerVersionGuardTests
{
    private const string Session = "PlaySessionId=play";

    private static readonly Guid _version = Guid.Parse("60000000-0000-0000-0000-00000000000a");

    // Every way to write a version a peer may not see, or that cannot be found.
    private static readonly string[] _hidden =
    [
        ProviderFixture.StubItem.ToString("N"),
        ProviderFixture.StubItem.ToString("D"),
        ProviderFixture.StubItem.ToString("B"),
        ProviderFixture.PrivateItem.ToString("N"),
        ProviderFixture.PrivateItem.ToString("D"),
        "7000000000000000000000000000000b",
        "7000000000000000000000000000000B",
        Guid.Empty.ToString("N"),
        Guid.Empty.ToString("D"),
        "not-an-id",
        _version.ToString("N") + "," + ProviderFixture.StubItem.ToString("N")
    ];

    private readonly ProviderFixture _fixture = new();
    private readonly PeerGuardFilter _filter;

    public PeerVersionGuardTests()
    {
        _filter = new PeerGuardFilter(_fixture.Directory, _fixture.Catalog, _fixture.Slots, _fixture.Closer, _fixture.Database.Object);
        _fixture.Own("play");
        _fixture.AddItem(_version, new Movie { Path = "/media/film/a-2160p.mkv" }, ProviderFixture.SharedLibrary);
    }

    public static TheoryData<string, string, string> PlaybackRoutes => new()
    {
        { "POST", "MediaInfo", "GetPostedPlaybackInfo" },
        { "GET", "MediaInfo", "GetPlaybackInfo" },
        { "GET", "Videos", "GetVideoStream" },
        { "HEAD", "Videos", "GetVideoStream" },
        { "GET", "Videos", "GetVideoStreamByContainer" },
        { "GET", "DynamicHls", "GetMasterHlsVideoPlaylist" },
        { "HEAD", "DynamicHls", "GetMasterHlsVideoPlaylist" },
        { "GET", "DynamicHls", "GetVariantHlsVideoPlaylist" },
        { "GET", "DynamicHls", "GetLiveHlsStream" },
        { "GET", "DynamicHls", "GetHlsVideoSegment" },
        { "GET", "Subtitle", "GetSubtitle" },
        { "GET", "Subtitle", "GetSubtitleWithTicks" },
        { "GET", "Subtitle", "GetSubtitlePlaylist" }
    };

    public static TheoryData<string> Hidden => new(_hidden);

    private static string Legacy(string version, int field = 2) => "params=" + Uri.EscapeDataString(new string(';', field) + version);

    private async Task<(int? Status, bool Ran)> RunAsync(string method, string controller, string action, string query, string? routeName = null, string? routeVersion = null, Dictionary<string, StringValues>? decoded = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.QueryString = new QueryString(query);
        if (decoded is not null)
        {
            http.Request.Query = new QueryCollection(decoded);
        }

        var route = new RouteData();
        route.Values[controller == "Subtitle" ? "routeItemId" : "itemId"] = ProviderFixture.SharedItem.ToString("N");
        if (routeName is not null)
        {
            route.Values[routeName] = routeVersion;
        }

        var actionContext = new ActionContext(http, route, ProviderFixture.Stock(controller, action));
        var context = new ResourceExecutingContext(actionContext, new List<IFilterMetadata>(), new List<IValueProviderFactory>());
        var ran = false;
        await _filter.OnResourceExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        });
        return ((context.Result as StatusCodeResult)?.StatusCode, ran);
    }

    private Task<(int? Status, bool Ran)> StreamAsync(string query) => RunAsync("GET", "Videos", "GetVideoStream", query);

    [Theory]
    [MemberData(nameof(PlaybackRoutes))]
    public async Task A_version_a_peer_may_not_see_is_not_found_on_any_route(string method, string controller, string action)
    {
        var (status, ran) = await RunAsync(method, controller, action, "?" + Session + "&mediaSourceId=" + ProviderFixture.StubItem.ToString("N"));

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    [Theory]
    [MemberData(nameof(PlaybackRoutes))]
    public async Task A_version_a_peer_may_see_is_served_on_every_route(string method, string controller, string action)
    {
        foreach (var id in new[] { ProviderFixture.SharedItem, _version })
        {
            foreach (var format in new[] { id.ToString("N"), id.ToString("D"), id.ToString("N").ToUpperInvariant() })
            {
                var (status, ran) = await RunAsync(method, controller, action, "?" + Session + "&mediaSourceId=" + format);

                Assert.True(ran);
                Assert.Null(status);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PlaybackRoutes))]
    public async Task A_request_that_names_no_version_is_served(string method, string controller, string action)
    {
        Assert.True((await RunAsync(method, controller, action, "?" + Session)).Ran);
        Assert.True((await RunAsync(method, controller, action, "?" + Session + "&mediaSourceId=")).Ran);
    }

    [Theory]
    [MemberData(nameof(Hidden))]
    public async Task A_version_in_the_query_is_checked_however_it_is_written(string version)
    {
        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?mediaSourceId=" + Uri.EscapeDataString(version)));
        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?MEDIASOURCEID=" + Uri.EscapeDataString(version.ToUpperInvariant())));
        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?static=true&MediaSourceId=" + Uri.EscapeDataString(version)));
    }

    [Theory]
    [MemberData(nameof(Hidden))]
    public async Task Every_value_of_a_repeated_version_is_checked(string version)
    {
        var allowed = "mediaSourceId=" + _version.ToString("N");
        var hidden = "mediaSourceId=" + Uri.EscapeDataString(version);

        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?" + allowed + "&" + hidden));
        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?" + hidden + "&" + allowed));
        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?mediaSourceId=&" + hidden));
    }

    [Fact]
    public async Task A_repeated_version_a_peer_may_see_is_served()
    {
        Assert.True((await StreamAsync("?mediaSourceId=" + _version.ToString("N") + "&mediaSourceId=" + ProviderFixture.SharedItem.ToString("D"))).Ran);
    }

    [Theory]
    [MemberData(nameof(Hidden))]
    public async Task A_version_in_the_legacy_params_is_checked(string version)
    {
        var seen = "mediaSourceId=" + _version.ToString("N");

        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?" + Legacy(version)));
        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?" + seen + "&" + Legacy(version)));
        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?" + Legacy(_version.ToString("N")) + "&" + Legacy(version)));
        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?" + Legacy(_version.ToString("N")) + "&mediaSourceId=" + Uri.EscapeDataString(version)));
    }

    [Fact]
    public async Task A_version_a_peer_may_see_in_the_legacy_params_is_served()
    {
        Assert.True((await StreamAsync("?" + Legacy(_version.ToString("N")))).Ran);
        Assert.True((await StreamAsync("?" + Legacy(string.Empty))).Ran);
    }

    [Theory]
    [MemberData(nameof(Hidden))]
    public async Task A_version_in_a_decoded_query_is_checked_under_every_case_of_its_name(string version)
    {
        var decoded = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["MediaSourceId"] = _version.ToString("N"),
            ["mediaSourceId"] = version
        };
        var legacy = new Dictionary<string, StringValues>(StringComparer.Ordinal)
        {
            ["Params"] = ";;" + _version.ToString("N"),
            ["params"] = ";;" + version
        };

        Assert.Equal((StatusCodes.Status404NotFound, false), await RunAsync("GET", "Videos", "GetVideoStream", string.Empty, decoded: decoded));
        Assert.Equal((StatusCodes.Status404NotFound, false), await RunAsync("GET", "Videos", "GetVideoStream", string.Empty, decoded: legacy));
    }

    [Theory]
    [MemberData(nameof(Hidden))]
    public async Task A_version_in_the_path_is_checked(string version)
    {
        foreach (var name in new[] { "mediaSourceId", "routeMediaSourceId" })
        {
            Assert.Equal((StatusCodes.Status404NotFound, false), await RunAsync("GET", "Subtitle", "GetSubtitle", string.Empty, name, version));
            Assert.Equal(
                (StatusCodes.Status404NotFound, false),
                await RunAsync("GET", "Subtitle", "GetSubtitle", "?mediaSourceId=" + Uri.EscapeDataString(version), name, _version.ToString("N")));
            Assert.True((await RunAsync("GET", "Subtitle", "GetSubtitle", string.Empty, name, _version.ToString("N"))).Ran);
        }
    }

    [Fact]
    public async Task A_refused_version_takes_no_slot()
    {
        _fixture.Connect(maxStreams: 1);

        await StreamAsync("?mediaSourceId=" + ProviderFixture.StubItem.ToString("N") + "&" + Session);

        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task A_refused_version_gets_the_same_answer_at_the_limit()
    {
        _fixture.Slots.TryEnter(_fixture.Peer.Id, 1, "other");

        Assert.Equal((StatusCodes.Status404NotFound, false), await StreamAsync("?mediaSourceId=" + ProviderFixture.StubItem.ToString("N")));
    }

    [Theory]
    [InlineData("VideoAttachments", "GetAttachment")]
    [InlineData("Trickplay", "GetTrickplayTileImage")]
    [InlineData("Trickplay", "GetTrickplayHlsPlaylist")]
    [InlineData("Library", "GetFile")]
    [InlineData("Library", "GetDownload")]
    [InlineData("HlsSegment", "GetHlsVideoSegmentLegacy")]
    [InlineData("Audio", "GetAudioStream")]
    public async Task A_route_closed_to_a_peer_stays_closed_whatever_version_it_names(string controller, string action)
    {
        foreach (var id in new[] { ProviderFixture.StubItem, _version })
        {
            Assert.Equal((StatusCodes.Status403Forbidden, false), await RunAsync("GET", controller, action, "?mediaSourceId=" + id.ToString("N"), "mediaSourceId", id.ToString("N")));
        }
    }

    [Theory]
    [InlineData("local")]
    [InlineData("admin")]
    [InlineData("anonymous")]
    public async Task Anyone_who_is_not_a_peer_names_any_version_as_before(string who)
    {
        var user = new User("mario", "auth", "reset");
        user.SetPermission(PermissionKind.IsAdministrator, who == "admin");
        _fixture.Caller = who == "anonymous" ? null : user;

        foreach (var route in PlaybackRoutes.Select(row => row.Cast<string>().ToArray()))
        {
            foreach (var version in _hidden)
            {
                var (status, ran) = await RunAsync(route[0], route[1], route[2], "?mediaSourceId=" + Uri.EscapeDataString(version) + "&" + Legacy(version));

                Assert.True(ran);
                Assert.Null(status);
            }
        }
    }
}
