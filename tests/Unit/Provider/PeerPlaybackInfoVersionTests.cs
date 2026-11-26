using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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
    private static readonly Guid _strm = Guid.Parse("60000000-0000-0000-0000-00000000000b");

    private readonly ProviderFixture _fixture = new();
    private readonly PeerPlaybackInfoFilter _filter;

    public PeerPlaybackInfoVersionTests()
    {
        _filter = new PeerPlaybackInfoFilter(_fixture.Directory, _fixture.Catalog, _fixture.Slots, _fixture.Closer);
        _fixture.AddItem(_version, new Movie { Path = "/media/film/a-2160p.mkv" }, ProviderFixture.SharedLibrary);
    }

    private static MediaSourceInfo Source(Guid id) => new() { Id = id.ToString("N") };

    private static OkObjectResult Answer(params MediaSourceInfo[] sources) => new(new PlaybackInfoResponse { PlaySessionId = "play", MediaSources = sources });

    // A source of a .strm as stock could give it, with the URL in every field that can hold one.
    private static MediaSourceInfo WithUrl(MediaSourceInfo source, string url)
    {
        source.Path = url;
        source.EncoderPath = url;
        source.RequiredHttpHeaders = new() { ["Authorization"] = url };
        source.MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Path = url }, new MediaStream { Index = 1, Type = MediaStreamType.Subtitle, IsExternal = true, Path = url, DeliveryUrl = url }];
        source.MediaAttachments = [new MediaAttachment { Index = 7, DeliveryUrl = url }];
        return source;
    }

    // Every text of an answer, through its objects, lists and dictionaries.
    private static List<string> TextsOf(object? value)
    {
        var texts = new List<string>();
        Collect(value, texts, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return texts;
    }

    private static void Collect(object? value, List<string> texts, HashSet<object> seen)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                texts.Add(text);
                return;
            case System.Collections.IDictionary dictionary:
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    Collect(entry.Key, texts, seen);
                    Collect(entry.Value, texts, seen);
                }

                return;
            case System.Collections.IEnumerable items:
                foreach (var item in items)
                {
                    Collect(item, texts, seen);
                }

                return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is Guid or DateTime or DateTimeOffset or TimeSpan or decimal || !seen.Add(value))
        {
            return;
        }

        foreach (var property in type.GetProperties().Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0))
        {
            Collect(property.GetValue(value), texts, seen);
        }
    }

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

    // Runs a PlaybackInfo of the item and returns the arguments the stock action would read, as they were when it ran.
    private async Task<Dictionary<string, object?>> ArgumentsSeenAsync(Guid item, object? body, Dictionary<string, object?>? query = null)
    {
        var arguments = new Dictionary<string, object?>(query ?? []) { ["itemId"] = item, ["playbackInfoDto"] = body };
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), ProviderFixture.Stock("MediaInfo", "GetPostedPlaybackInfo"));
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, new object());
        Dictionary<string, object?>? seen = null;
        await _filter.OnActionExecutionAsync(context, () =>
        {
            seen = new Dictionary<string, object?>(context.ActionArguments);
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()) { Result = Answer(Source(item)) });
        });
        return seen ?? throw new InvalidOperationException("the action did not run");
    }

    private static MediaSourceInfo Direct(MediaSourceInfo source)
    {
        source.SupportsDirectPlay = true;
        source.SupportsDirectStream = true;
        return source;
    }

    private static (bool? Play, bool? Stream) DirectOf(Dictionary<string, object?> arguments)
        => (arguments.GetValueOrDefault("enableDirectPlay") as bool?, arguments.GetValueOrDefault("enableDirectStream") as bool?);

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

    // Row 565: the path of an admin's .strm is the URL it holds, and no field of the answer gives it to a peer.
    [Fact]
    public async Task The_answer_gives_no_url_of_the_strm_of_the_admin()
    {
        const string Secret = "http://origin.invalid/film.mkv?api_key=strm-secret";
        _fixture.AddItem(_strm, new Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);
        var answer = Answer(Source(ProviderFixture.SharedItem), WithUrl(Source(_strm), Secret));

        await RunAsync(answer, new PostedBody());

        Assert.Equal([ProviderFixture.SharedItem.ToString("N"), _strm.ToString("N")], Ids(answer));
        Assert.DoesNotContain(TextsOf(answer.Value), text => text.Contains("strm-secret", StringComparison.Ordinal));
        Assert.True(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
    }

    // A source that reads anything but a file of this server holds a URL too, whatever its item.
    [Theory]
    [InlineData(MediaProtocol.Http, false)]
    [InlineData(MediaProtocol.File, true)]
    public async Task The_answer_gives_no_url_of_a_source_that_is_not_a_file(MediaProtocol protocol, bool isRemote)
    {
        const string Secret = "http://origin.invalid/film.mkv?api_key=strm-secret";
        var source = WithUrl(Source(ProviderFixture.SharedItem), Secret);
        source.Protocol = protocol;
        source.IsRemote = isRemote;
        var answer = Answer(source);

        await RunAsync(answer, new PostedBody());

        Assert.DoesNotContain(TextsOf(answer.Value), text => text.Contains("strm-secret", StringComparison.Ordinal));
    }

    // Rows 568 and 569: no path of this server leaves, as in the catalog, only the extension of the file; what a
    // consumer needs to play the file stays.
    [Fact]
    public async Task The_answer_for_a_file_of_this_server_gives_no_path_and_keeps_the_rest()
    {
        const string Subtitle = "/Videos/a/a/Subtitles/1/0/Stream.vtt";
        var source = Source(ProviderFixture.SharedItem);
        source.Path = "/media/film/a.mkv";
        source.EncoderPath = "/media/film/a.mkv";
        source.RequiredHttpHeaders = new() { ["X-Kept"] = "kept" };
        source.MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Path = "/media/film/a.mkv" },
            new MediaStream { Index = 1, Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/media/film/a.it.srt", DeliveryUrl = Subtitle }
        ];
        source.MediaAttachments = [new MediaAttachment { Index = 7, DeliveryUrl = "/Videos/a/a/Attachments/7" }];
        var answer = Answer(source);

        await RunAsync(answer, new PostedBody());

        var sent = Assert.Single(((PlaybackInfoResponse)answer.Value!).MediaSources);
        Assert.Equal(".mkv", sent.Path);
        Assert.Null(sent.EncoderPath);
        Assert.All(sent.MediaStreams, stream => Assert.Null(stream.Path));
        Assert.Equal("kept", sent.RequiredHttpHeaders["X-Kept"]);
        Assert.Equal(Subtitle, sent.MediaStreams[1].DeliveryUrl);
        Assert.Equal("/Videos/a/a/Attachments/7", sent.MediaAttachments[0].DeliveryUrl);
    }

    // Row 569: the consumer picks the container of its stream URL from the extension, over the first name of Container.
    [Theory]
    [InlineData("/media/film/a.mp4", "mov,mp4,m4a,3gp,3g2,mj2", "mp4")]
    [InlineData("/media/film/a.MKV", "mkv,webm", "mkv")]
    [InlineData("/media/music/a.m4a", "mov,mp4,m4a,3gp,3g2,mj2", "m4a")]
    public async Task A_file_of_this_server_keeps_its_container_for_the_consumer(string path, string container, string expected)
    {
        var source = Source(ProviderFixture.SharedItem);
        source.Path = path;
        source.Container = container;
        var answer = Answer(source);

        await RunAsync(answer, new PostedBody());

        var sent = Assert.Single(((PlaybackInfoResponse)answer.Value!).MediaSources);
        Assert.Equal(expected, PlaybackResponseBuilder.ContainerOf(sent));
    }

    [Fact]
    public async Task An_answer_left_with_no_version_is_not_found_and_holds_no_slot()
    {
        var (result, _) = await RunAsync(Answer(Source(ProviderFixture.StubItem), Source(ProviderFixture.PrivateItem)), new PostedBody());

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Equal(0, _fixture.Slots.Count(_fixture.Peer.Id));
        Assert.False(_fixture.Slots.Owns(_fixture.Peer.Id, "play"));
    }

    // Row 570: a peer gets 404 for the direct streams of an admin's .strm, so stock must offer it only transcoded.
    [Fact]
    public async Task A_peer_playback_of_the_strm_of_the_admin_turns_direct_play_and_stream_off()
    {
        _fixture.AddItem(_strm, new Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);

        var seen = await ArgumentsSeenAsync(_strm, new PostedBody());

        Assert.Equal((false, false), DirectOf(seen));
    }

    [Fact]
    public async Task A_peer_cannot_turn_direct_play_back_on_for_the_strm_of_the_admin()
    {
        _fixture.AddItem(_strm, new Movie { Path = "/media/film/remote.STRM" }, ProviderFixture.SharedLibrary);

        var seen = await ArgumentsSeenAsync(_strm, new PostedBody(), new() { ["enableDirectPlay"] = true, ["enableDirectStream"] = true });

        Assert.Equal((false, false), DirectOf(seen));
    }

    [Fact]
    public async Task A_version_named_in_the_body_that_is_the_strm_of_the_admin_plays_transcoded()
    {
        _fixture.AddItem(_strm, new Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);

        var seen = await ArgumentsSeenAsync(ProviderFixture.SharedItem, new PostedBody { MediaSourceId = _strm.ToString("N") });

        Assert.Equal((false, false), DirectOf(seen));
    }

    [Fact]
    public async Task A_version_named_in_the_query_that_is_the_strm_of_the_admin_plays_transcoded()
    {
        _fixture.AddItem(_strm, new Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);

        var seen = await ArgumentsSeenAsync(ProviderFixture.SharedItem, new PostedBody(), new() { ["mediaSourceId"] = _strm.ToString("N") });

        Assert.Equal((false, false), DirectOf(seen));
    }

    // Stock reads the version of the query over the body's, so the file the query names is what plays.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_version_of_an_item_that_is_a_strm_keeps_its_direct_play(bool inQuery)
    {
        _fixture.AddItem(_strm, new Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);
        var file = ProviderFixture.SharedItem.ToString("N");
        var query = new Dictionary<string, object?> { ["enableDirectPlay"] = true };
        if (inQuery)
        {
            query["mediaSourceId"] = file;
        }

        var seen = await ArgumentsSeenAsync(_strm, new PostedBody { MediaSourceId = inQuery ? _strm.ToString("N") : file }, query);

        Assert.Equal((true, null), DirectOf(seen));
    }

    [Fact]
    public async Task A_file_of_this_server_keeps_the_direct_play_the_peer_asks_for()
    {
        var seen = await ArgumentsSeenAsync(ProviderFixture.SharedItem, new PostedBody(), new() { ["enableDirectPlay"] = true, ["enableDirectStream"] = true });

        Assert.Equal((true, true), DirectOf(seen));
    }

    // The answer itself never offers a peer the direct play of an admin's .strm: a read without the arguments (GET) or a
    // version stock gave beside the one asked for.
    [Theory]
    [InlineData("GetPostedPlaybackInfo")]
    [InlineData("GetPlaybackInfo")]
    public async Task The_answer_offers_no_direct_play_of_the_strm_of_the_admin_and_keeps_that_of_a_file(string action)
    {
        _fixture.AddItem(_strm, new Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);
        var answer = Answer(Direct(Source(ProviderFixture.SharedItem)), Direct(Source(_strm)));

        await RunAsync(answer, new PostedBody(), action);

        var sources = ((PlaybackInfoResponse)answer.Value!).MediaSources;
        Assert.True(sources[0].SupportsDirectPlay);
        Assert.True(sources[0].SupportsDirectStream);
        Assert.False(sources[1].SupportsDirectPlay);
        Assert.False(sources[1].SupportsDirectStream);
    }

    [Fact]
    public async Task Anyone_who_is_not_a_peer_plays_the_strm_of_the_admin_as_before()
    {
        _fixture.Caller = new User("mario", "auth", "reset");
        _fixture.AddItem(_strm, new Movie { Path = "/media/film/remote.strm" }, ProviderFixture.SharedLibrary);

        var seen = await ArgumentsSeenAsync(_strm, new PostedBody(), new() { ["enableDirectPlay"] = true });
        var answer = Answer(Direct(Source(_strm)));
        await RunAsync(answer, new PostedBody());

        Assert.Equal((true, null), DirectOf(seen));
        Assert.True(((PlaybackInfoResponse)answer.Value!).MediaSources[0].SupportsDirectPlay);
        Assert.True(((PlaybackInfoResponse)answer.Value!).MediaSources[0].SupportsDirectStream);
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
