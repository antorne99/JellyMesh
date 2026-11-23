using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class PeerPlaybackInfoVersionTests
{
    private static readonly Guid _version = Guid.Parse("60000000-0000-0000-0000-00000000000a");

    private readonly ProviderFixture _fixture = new();
    private readonly PeerPlaybackInfoFilter _filter;

    public PeerPlaybackInfoVersionTests()
    {
        _filter = new PeerPlaybackInfoFilter(_fixture.Directory, _fixture.Catalog, _fixture.Slots, _fixture.Closer);
        _fixture.AddItem(_version, new Movie { Path = "/media/film/a-2160p.mkv" }, ProviderFixture.SharedLibrary);
    }

    private static MediaSourceInfo Source(Guid id) => new() { Id = id.ToString("N") };

    private static OkObjectResult Answer(params MediaSourceInfo[] sources) => new(new PlaybackInfoResponse { PlaySessionId = "play", MediaSources = sources });

    private static string[] Ids(OkObjectResult answer) => ((PlaybackInfoResponse)answer.Value!).MediaSources.Select(source => source.Id).ToArray();

    private async Task<(IActionResult? Result, bool Ran)> RunAsync(IActionResult result, object? body, string action = "GetPostedPlaybackInfo")
    {
        var arguments = new Dictionary<string, object?> { ["itemId"] = ProviderFixture.SharedItem };
        if (action == "GetPostedPlaybackInfo")
        {
            arguments["playbackInfoDto"] = body;
        }

        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), ProviderFixture.Stock("MediaInfo", action));
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, new object());
        var executed = new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()) { Result = result };
        var ran = false;
        await _filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(executed);
        });
        return (ran ? executed.Result : context.Result, ran);
    }

    private static int? StatusOf(IActionResult? result) => (result as StatusCodeResult)?.StatusCode;

    // The posted body as stock binds it.
    private sealed class PostedBody
    {
        public string? LiveStreamId { get; init; }

        public string? MediaSourceId { get; init; }
    }

    // A body whose version has another name: a later Jellyfin that changed it, so the check cannot see it.
    private sealed class RenamedBody
    {
        public string? LiveStreamId { get; init; }

        public string? Version { get; init; }
    }

    [Theory]
    [InlineData("50000000000000000000000000000000")]
    [InlineData("50000000-0000-0000-0000-000000000000")]
    [InlineData("40000000000000000000000000000000")]
    [InlineData("7000000000000000000000000000000b")]
    [InlineData("7000000000000000000000000000000B")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-an-id")]
    public async Task A_body_that_names_a_version_a_peer_may_not_see_is_not_found(string version)
    {
        var (result, ran) = await RunAsync(Answer(Source(ProviderFixture.SharedItem)), new PostedBody { MediaSourceId = version });

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
    }

    [Fact]
    public async Task A_body_that_cannot_be_checked_for_a_version_is_not_found()
    {
        var (result, ran) = await RunAsync(Answer(Source(ProviderFixture.SharedItem)), new RenamedBody { Version = ProviderFixture.StubItem.ToString("N") });

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
    }

    [Theory]
    [InlineData("30000000000000000000000000000000")]
    [InlineData("30000000-0000-0000-0000-000000000000")]
    [InlineData("6000000000000000000000000000000A")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_body_that_names_a_version_a_peer_may_see_or_none_passes(string? version)
    {
        var (_, ran) = await RunAsync(Answer(Source(ProviderFixture.SharedItem)), new PostedBody { MediaSourceId = version });

        Assert.True(ran);
        Assert.True(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
    }

    [Theory]
    [InlineData("GetPostedPlaybackInfo")]
    [InlineData("GetPlaybackInfo")]
    public async Task The_answer_keeps_the_versions_a_peer_may_see(string action)
    {
        var answer = Answer(Source(ProviderFixture.StubItem), Source(ProviderFixture.SharedItem), Source(ProviderFixture.PrivateItem), Source(_version), new MediaSourceInfo());

        await RunAsync(answer, new PostedBody(), action);

        Assert.Equal([ProviderFixture.SharedItem.ToString("N"), _version.ToString("N")], Ids(answer));
        Assert.True(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
    }

    [Fact]
    public async Task An_answer_left_with_no_version_is_not_found_and_holds_no_slot()
    {
        var (result, _) = await RunAsync(Answer(Source(ProviderFixture.StubItem), Source(ProviderFixture.PrivateItem)), new PostedBody());

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.False(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
    }

    [Fact]
    public async Task Anyone_who_is_not_a_peer_gets_the_answer_as_before()
    {
        _fixture.Caller = new User("mario", "auth", "reset");
        var answer = Answer(Source(ProviderFixture.StubItem), Source(ProviderFixture.SharedItem), Source(ProviderFixture.PrivateItem));

        var (result, ran) = await RunAsync(answer, new PostedBody { MediaSourceId = ProviderFixture.StubItem.ToString("N") });

        Assert.True(ran);
        Assert.Same(answer, result);
        Assert.Equal(3, Ids(answer).Length);
    }

    [Fact]
    public void The_filter_of_a_peer_runs_around_the_one_that_answers_for_the_versions()
    {
        var services = new ServiceCollection();
        services.AddPlayback();
        services.AddProvider();
        using var provider = services.BuildServiceProvider();
        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters.OfType<ServiceFilterAttribute>();

        var ordered = filters.OrderBy(filter => filter.Order).Select(filter => filter.ServiceType).ToList();

        Assert.True(ordered.IndexOf(typeof(PeerPlaybackInfoFilter)) < ordered.IndexOf(typeof(PlaybackInfoFilter)));
    }
}
