using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class ProviderClientTests
{
    private static readonly RemoteEndpoint _endpoint = new(new Uri(TestValues.Url + "/"), "device");
    private static readonly RemoteSession _session = new(TestValues.Token, "remote-user");
    private static readonly RemotePlaybackRequest _request = new() { ItemId = "item", MediaSourceId = "ms" };

    private static HttpResponseMessage OverLimit()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation(ProviderProtocol.RefusalHeader, ProviderProtocol.StreamLimitRefusal);
        return response;
    }

    private static RemotePlaybackClient Playback(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new RemotePlaybackClient(new StubHttpClientFactory(handler));
    }

    private static JellyfinRemoteClient Catalog(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new JellyfinRemoteClient(new StubHttpClientFactory(handler));
    }

    [Fact]
    public async Task The_signal_on_a_playback_info_is_the_stream_limit()
    {
        var client = Playback(_ => OverLimit(), out _);

        var result = await client.GetPlaybackInfoAsync(_endpoint, _session, _request, CancellationToken.None);

        Assert.Equal(RemoteFailure.StreamLimitReached, result.Failure);
    }

    [Fact]
    public async Task The_signal_on_a_stream_is_the_stream_limit()
    {
        var client = Playback(_ => OverLimit(), out _);

        var result = await client.OpenStreamAsync(_endpoint, _session, new RemoteStreamRequest(HttpMethod.Get, "/Videos/x/stream", null), CancellationToken.None);

        Assert.Equal(RemoteFailure.StreamLimitReached, result.Failure);
    }

    [Fact]
    public async Task A_429_without_the_signal_is_not_a_limit_so_a_stock_remote_can_never_report_one()
    {
        var client = Playback(_ => StubHandler.Status(HttpStatusCode.TooManyRequests), out _);

        var result = await client.GetPlaybackInfoAsync(_endpoint, _session, _request, CancellationToken.None);

        Assert.Equal(RemoteFailure.ServerError, result.Failure);
    }

    [Fact]
    public async Task The_signal_on_another_status_is_not_a_limit()
    {
        var client = Playback(_ =>
        {
            var response = StubHandler.Status(HttpStatusCode.InternalServerError);
            response.Headers.TryAddWithoutValidation(ProviderProtocol.RefusalHeader, ProviderProtocol.StreamLimitRefusal);
            return response;
        }, out _);

        var result = await client.GetPlaybackInfoAsync(_endpoint, _session, _request, CancellationToken.None);

        Assert.Equal(RemoteFailure.ServerError, result.Failure);
    }

    [Fact]
    public async Task A_replacement_names_the_session_it_replaces()
    {
        HttpRequestMessage? seen = null;
        var client = Playback(request =>
        {
            seen = request;
            return StubHandler.Json("{}");
        }, out _);

        await client.GetPlaybackInfoAsync(_endpoint, _session, _request with { ReplacesPlaySessionId = "old" }, CancellationToken.None);

        Assert.Equal("old", seen!.Headers.GetValues(ProviderProtocol.ReplacesHeader).Single());
    }

    [Fact]
    public async Task A_stock_request_carries_nothing_of_the_protocol()
    {
        HttpRequestMessage? seen = null;
        var client = Playback(request =>
        {
            seen = request;
            return StubHandler.Json("{}");
        }, out _);

        await client.GetPlaybackInfoAsync(_endpoint, _session, _request, CancellationToken.None);

        Assert.False(seen!.Headers.Contains(ProviderProtocol.ReplacesHeader));
    }

    [Fact]
    public async Task Release_calls_the_provider_api()
    {
        var client = Playback(_ => StubHandler.Status(HttpStatusCode.NoContent), out var handler);

        var result = await client.ReleaseStreamAsync(_endpoint, _session, "play/session", CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("/JellyMesh/Provider/Streams/play%2Fsession", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task The_capability_contract_holds_a_200_with_a_version_is_jellymesh()
    {
        var client = Catalog(_ => StubHandler.Json("{\"ProtocolVersion\":" + ProviderProtocol.Version + "}"), out var handler);

        var result = await client.DetectCapabilitiesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(RemoteMode.JellyMesh, result.Value!.Mode);
        Assert.Equal(ProviderProtocol.Version, result.Value.ProtocolVersion);
        Assert.Equal("/JellyMesh/Capabilities", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task The_capability_contract_holds_a_404_is_standard()
    {
        var client = Catalog(_ => StubHandler.Status(HttpStatusCode.NotFound), out _);

        var result = await client.DetectCapabilitiesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(RemoteMode.Standard, result.Value!.Mode);
    }

    [Fact]
    public async Task Shared_libraries_come_from_the_provider_api()
    {
        var client = Catalog(_ => StubHandler.Json(TestValues.ViewsJson), out var handler);

        var result = await client.GetSharedLibrariesAsync(_endpoint, _session, CancellationToken.None);

        Assert.Equal(2, result.Value!.Count);
        Assert.Equal("/JellyMesh/Provider/Libraries", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Items_via_the_provider_use_its_route()
    {
        var client = Catalog(_ => StubHandler.Json("{\"Items\":[],\"TotalRecordCount\":0}"), out var handler);

        await client.GetItemsAsync(_endpoint, _session, new RemoteItemQuery("lib", RemoteItemType.Movie, 0, 10, null, null, true), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/JellyMesh/Provider/Items", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Stock_items_stay_on_the_stock_route()
    {
        var client = Catalog(_ => StubHandler.Json("{\"Items\":[],\"TotalRecordCount\":0}"), out var handler);

        await client.GetItemsAsync(_endpoint, _session, new RemoteItemQuery("lib", RemoteItemType.Movie, 0, 10), CancellationToken.None);

        Assert.Equal("/Items", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Quality_of_the_provider_lands_on_its_version()
    {
        const string Json = "{\"Items\":[{\"Id\":\"i\",\"MediaSources\":[{\"Id\":\"ms\"},{\"Id\":\"other\"}]}],\"TotalRecordCount\":1,"
            + "\"Quality\":{\"ms\":{\"Height\":2160,\"Codec\":\"av1\",\"VideoRange\":\"HDR\"}}}";
        var client = Catalog(_ => StubHandler.Json(Json), out _);

        var result = await client.GetItemsAsync(_endpoint, _session, new RemoteItemQuery("lib", RemoteItemType.Movie, 0, 10, null, null, true), CancellationToken.None);

        var sources = result.Value!.Items[0].MediaSources!;
        Assert.Equal(2160, sources[0].Quality!.Height);
        Assert.Null(sources[1].Quality);
    }

    [Fact]
    public async Task The_feed_is_read_with_its_cursor()
    {
        const string Json = "{\"Reset\":false,\"Cursor\":\"next\",\"HasMore\":true,\"Changes\":[{\"Id\":\"i\",\"Type\":\"Movie\",\"LibraryIds\":[\"lib\"]}]}";
        var client = Catalog(_ => StubHandler.Json(Json), out var handler);

        var result = await client.GetChangesAsync(_endpoint, _session, "a b", CancellationToken.None);

        Assert.Equal("next", result.Value!.Cursor);
        Assert.True(result.Value.HasMore);
        Assert.Equal("i", Assert.Single(result.Value.Changes).Id);
        Assert.Equal("?cursor=a%20b", Assert.Single(handler.Requests).Uri.Query);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Cursor\":\"\",\"Changes\":[]}")]
    [InlineData("{\"Cursor\":\"x\"}")]
    [InlineData("not json")]
    public async Task A_feed_that_makes_no_sense_is_refused(string json)
    {
        var client = Catalog(_ => StubHandler.Json(json), out _);

        var result = await client.GetChangesAsync(_endpoint, _session, null, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, result.Failure);
    }

    [Theory]
    [InlineData("feed")]
    [InlineData("libraries")]
    [InlineData("items")]
    public async Task No_redirect_of_the_provider_api_is_followed_to_another_host(string call)
    {
        var client = Catalog(_ => StubHandler.Redirect(HttpStatusCode.Found, "http://evil.example.invalid/x"), out var handler);

        var failure = call switch
        {
            "feed" => (await client.GetChangesAsync(_endpoint, _session, null, CancellationToken.None)).Failure,
            "libraries" => (await client.GetSharedLibrariesAsync(_endpoint, _session, CancellationToken.None)).Failure,
            _ => (await client.GetItemsAsync(_endpoint, _session, new RemoteItemQuery("lib", RemoteItemType.Movie, 0, 10, null, null, true), CancellationToken.None)).Failure
        };

        Assert.Equal(RemoteFailure.RedirectRefused, failure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_oversized_answer_of_the_provider_api_is_refused()
    {
        var factory = new StubHttpClientFactory(new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ConfigurationLimitExceeded)));
        var client = new JellyfinRemoteClient(factory);

        var result = await client.GetChangesAsync(_endpoint, _session, null, CancellationToken.None);

        Assert.Equal(RemoteFailure.ResponseTooLarge, result.Failure);
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_in_time_is_a_timeout()
    {
        var factory = new StubHttpClientFactory(new StubHandler(_ => throw new TaskCanceledException()));
        var client = new JellyfinRemoteClient(factory);

        var result = await client.GetChangesAsync(_endpoint, _session, null, CancellationToken.None);

        Assert.Equal(RemoteFailure.Timeout, result.Failure);
    }

    [Fact]
    public void Quality_sent_by_a_provider_wins_over_what_the_streams_say()
    {
        var source = new RemoteMediaSource
        {
            Id = "ms",
            Quality = new RemoteQuality(2160, 3840, 9, "av1", "HDR", "HDR10"),
            MediaStreams = [new RemoteStreamInfo("Video", "h264", 480, null, null, null, null, false)]
        };

        var entry = RemoteItemMapper.ToIndexedItem(new RemoteItem { Id = "i", MediaSources = [source] }, RemoteItemType.Movie, "lib", DateTimeOffset.UnixEpoch);

        var version = Assert.Single(entry.Versions);
        Assert.Equal(2160, version.Height);
        Assert.Equal("av1", version.Codec);
        Assert.Equal("HDR", version.VideoRange);
    }

    [Fact]
    public void Without_quality_the_streams_decide_as_on_a_stock_remote()
    {
        var source = new RemoteMediaSource { Id = "ms", MediaStreams = [new RemoteStreamInfo("Video", "h264", 720, null, 5, null, null, false)] };

        var entry = RemoteItemMapper.ToIndexedItem(new RemoteItem { Id = "i", MediaSources = [source] }, RemoteItemType.Movie, "lib", DateTimeOffset.UnixEpoch);

        Assert.Equal(720, Assert.Single(entry.Versions).Height);
    }

    [Fact]
    public void Decision_11_stays_a_stub_of_another_server_is_not_imported_from_any_route()
    {
        var stub = new RemoteMediaSource { Id = "ms", Path = "http://127.0.0.1:8096/JellyMesh/Play/stub", Quality = new RemoteQuality(1080, 1920, 1, "h264", "SDR", "SDR") };

        var entry = RemoteItemMapper.ToIndexedItem(new RemoteItem { Id = "i", MediaSources = [stub] }, RemoteItemType.Movie, "lib", DateTimeOffset.UnixEpoch);

        Assert.Empty(entry.Versions);
    }
}

public sealed class FeedSyncTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<RemoteServerInfo> AddPeerAsync()
    {
        _fixture.Client.Capabilities = new RemoteCapabilities(RemoteMode.JellyMesh, 1);
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("old", "Old"));
        return await _fixture.AddServerAsync("peer", "films");
    }

    private static RemoteResult<RemoteChangePage> Page(string cursor, params string[] ids)
    {
        return RemoteResult.Success(new RemoteChangePage(false, cursor, false, ids.Select(id => new RemoteChange(id, "Movie", ["films"])).ToList()));
    }

    private void FeedGives(params string[] ids)
    {
        _fixture.Client.Feed = cursor => cursor is null
            ? RemoteResult.Success(new RemoteChangePage(true, "cursor-0", false, []))
            : Page("cursor-1", ids);
    }

    private IReadOnlyList<string> Indexed(Guid serverId)
    {
        return _fixture.Index.Snapshot.Find(serverId)!.Items.Select(item => item.ItemId).ToList();
    }

    [Fact]
    public async Task A_full_read_of_a_peer_goes_through_the_provider_api()
    {
        var server = await AddPeerAsync();

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.All(_fixture.Client.Queries, query => Assert.True(query.ViaProvider));
        Assert.True(_fixture.Client.SharedLibraryReads > 0);
        Assert.Equal(["old"], Indexed(server.Id));
    }

    [Fact]
    public async Task A_full_read_asks_where_the_feed_stands_before_reading()
    {
        await AddPeerAsync();
        var order = new List<string>();
        _fixture.Client.Feed = _ =>
        {
            order.Add("feed");
            return RemoteResult.Success(new RemoteChangePage(true, "cursor-0", false, []));
        };
        _fixture.Client.OnPage = (_, _) =>
        {
            order.Add("page");
            return RemoteFailure.None;
        };

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal("feed", order[0]);
    }

    [Fact]
    public async Task An_incremental_sync_of_a_peer_reads_the_feed_and_never_polls()
    {
        var server = await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Queries.Clear();
        _fixture.Client.ItemsById.Add(TestItems.Movie("new", "New", tmdb: "2"));
        FeedGives("new");

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        var query = Assert.Single(_fixture.Client.Queries);
        Assert.True(query.ViaProvider);
        Assert.Null(query.ChangedSinceUtc);
        Assert.Equal(["new"], query.Ids);
        Assert.Equal(["old", "new"], Indexed(server.Id));
        Assert.Equal("cursor-0", _fixture.Client.FeedReads[^1]);
    }

    [Fact]
    public async Task The_next_read_goes_on_from_the_new_cursor()
    {
        await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        FeedGives();

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);
        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal("cursor-1", _fixture.Client.FeedReads[^1]);
    }

    [Fact]
    public async Task Nothing_changed_reads_no_items()
    {
        await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Queries.Clear();
        FeedGives();

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Empty(_fixture.Client.Queries);
    }

    [Fact]
    public async Task All_pages_of_the_feed_are_read()
    {
        var server = await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.ItemsById.Add(TestItems.Movie("a", "A", tmdb: "2"));
        _fixture.Client.ItemsById.Add(TestItems.Movie("b", "B", tmdb: "3"));
        _fixture.Client.Feed = cursor => cursor switch
        {
            "cursor-0" => RemoteResult.Success(new RemoteChangePage(false, "cursor-1", true, [new RemoteChange("a", "Movie", ["films"])])),
            _ => RemoteResult.Success(new RemoteChangePage(false, "cursor-2", false, [new RemoteChange("b", "Movie", ["films"])]))
        };

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["old", "a", "b"], Indexed(server.Id));
    }

    [Fact]
    public async Task A_feed_that_does_not_move_is_refused()
    {
        var server = await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Feed = _ => RemoteResult.Success(new RemoteChangePage(false, "cursor-0", true, []));

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidResponse, _fixture.Index.Snapshot.Find(server.Id)!.LastFailure);
    }

    [Fact]
    public async Task A_change_in_a_library_not_imported_is_ignored()
    {
        var server = await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Queries.Clear();
        _fixture.Client.ItemsById.Add(TestItems.Movie("new", "New", tmdb: "2"));
        _fixture.Client.Feed = _ => RemoteResult.Success(new RemoteChangePage(false, "cursor-1", false, [new RemoteChange("new", "Movie", ["shows"])]));

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Empty(_fixture.Client.Queries);
        Assert.Equal(["old"], Indexed(server.Id));
    }

    [Fact]
    public async Task A_change_of_an_unknown_type_is_ignored()
    {
        await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Queries.Clear();
        _fixture.Client.Feed = _ => RemoteResult.Success(new RemoteChangePage(false, "cursor-1", false, [new RemoteChange("x", "Trailer", ["films"])]));

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Empty(_fixture.Client.Queries);
    }

    [Fact]
    public async Task A_reset_of_the_feed_means_a_full_read()
    {
        await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Queries.Clear();
        _fixture.Client.Feed = _ => RemoteResult.Success(new RemoteChangePage(true, "cursor-new", false, []));

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Contains(_fixture.Client.Queries, query => query.LibraryId == "films" && query.Ids is null);
        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);
        Assert.Contains("cursor-new", _fixture.Client.FeedReads);
    }

    [Fact]
    public async Task Without_a_cursor_as_after_a_restart_a_full_read_comes_first()
    {
        await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        var restarted = new CatalogSyncService(_fixture.Registry, _fixture.Reader, _fixture.Index, _fixture.Vault, _fixture.Clock, _fixture.Logger);
        _fixture.Client.Queries.Clear();

        await restarted.RunIncrementalAsync(CancellationToken.None);

        Assert.Contains(_fixture.Client.Queries, query => query.LibraryId == "films" && query.Ids is null);
        restarted.Dispose();
    }

    [Fact]
    public async Task A_feed_that_fails_keeps_the_entries_and_marks_the_server()
    {
        var server = await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Feed = _ => RemoteResult.Fail<RemoteChangePage>(RemoteFailure.Unreachable);

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        var catalog = _fixture.Index.Snapshot.Find(server.Id)!;
        Assert.Equal(ServerSyncState.Stale, catalog.State);
        Assert.Equal(RemoteFailure.Unreachable, catalog.LastFailure);
        Assert.Equal(["old"], Indexed(server.Id));
    }

    [Fact]
    public async Task The_feed_cannot_remove_anything_that_stays_with_the_reconciliation()
    {
        var server = await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        FeedGives("gone");

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Equal(["old"], Indexed(server.Id));
        Assert.Equal(ServerSyncState.Complete, _fixture.Index.Snapshot.Find(server.Id)!.State);
    }

    [Fact]
    public async Task The_reconciliation_still_reads_everything_on_a_peer()
    {
        var server = await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Items.Clear();

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Empty(Indexed(server.Id));
    }

    [Fact]
    public async Task A_stock_remote_keeps_being_polled()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("old", "Old"));
        await _fixture.AddServerAsync("stock", "films");
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Queries.Clear();

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.Empty(_fixture.Client.FeedReads);
        Assert.All(_fixture.Client.Queries, query =>
        {
            Assert.False(query.ViaProvider);
            Assert.NotNull(query.ChangedSinceUtc);
        });
    }

    [Fact]
    public async Task A_server_connected_after_it_was_added_moves_to_the_provider_api_at_the_next_sync()
    {
        _fixture.Client.Add("films", RemoteItemType.Movie, TestItems.Movie("old", "Old"));
        var server = await _fixture.AddServerAsync("later", "films");
        Assert.Equal(RemoteMode.Standard, server.Mode);
        _fixture.Client.Capabilities = new RemoteCapabilities(RemoteMode.JellyMesh, 1);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.All(_fixture.Client.Queries, query => Assert.True(query.ViaProvider));
        Assert.Equal(RemoteMode.JellyMesh, _fixture.Registry.GetServers().Single().Mode);
    }

    [Fact]
    public async Task A_peer_that_loses_the_plugin_goes_back_to_polling()
    {
        await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.Queries.Clear();
        _fixture.Client.Capabilities = new RemoteCapabilities(RemoteMode.Standard, 0);

        await _fixture.Sync.RunIncrementalAsync(CancellationToken.None);

        Assert.All(_fixture.Client.Queries, query => Assert.False(query.ViaProvider));
        Assert.Contains(_fixture.Client.Queries, query => query.ChangedSinceUtc is not null);
    }

    [Fact]
    public async Task A_failed_detection_leaves_the_mode_as_it_was()
    {
        await AddPeerAsync();
        _fixture.Client.CapabilityFailure = RemoteFailure.ServerError;

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(RemoteMode.JellyMesh, _fixture.Registry.GetServers().Single().Mode);
    }

    [Fact]
    public async Task A_server_with_nothing_chosen_is_not_called()
    {
        await _fixture.AddServerAsync("empty");
        _fixture.Client.Capabilities = new RemoteCapabilities(RemoteMode.JellyMesh, 1);

        await _fixture.Sync.RunAsync(CancellationToken.None);

        Assert.Equal(RemoteMode.Standard, _fixture.Registry.GetServers().Single().Mode);
    }

    [Fact]
    public async Task The_libraries_offered_by_a_peer_are_what_it_shares()
    {
        var server = await AddPeerAsync();
        _fixture.Client.SharedLibraries = [new RemoteLibrary("films", "Film", "movies")];

        var libraries = await _fixture.Registry.GetLibrariesAsync(server.Id, CancellationToken.None);

        Assert.Equal("films", Assert.Single(libraries.Value!).Id);
    }

    [Fact]
    public async Task A_library_the_peer_stopped_sharing_fails_the_read_and_keeps_the_entries()
    {
        var server = await AddPeerAsync();
        await _fixture.Sync.RunAsync(CancellationToken.None);
        _fixture.Client.SharedLibraries = [];

        await _fixture.Sync.RunAsync(CancellationToken.None);

        var catalog = _fixture.Index.Snapshot.Find(server.Id)!;
        Assert.Equal(RemoteFailure.LibraryUnavailable, catalog.LastFailure);
        Assert.Equal(["old"], Indexed(server.Id));
    }

    [Fact]
    public async Task Testing_a_peer_reads_what_it_shares()
    {
        var server = await AddPeerAsync();
        _fixture.Client.SharedLibraries = [new RemoteLibrary("films", "Film", "movies")];

        var report = await _fixture.Registry.TestAsync(server.Id, CancellationToken.None);

        Assert.Equal(1, report.Value!.LibraryCount);
    }
}

public class StreamLimitPlaybackTests
{
    private readonly CoordinatorFixture _fixture = new();

    private static RemoteResult<PlaybackInfoResponse> OverLimit() => RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.StreamLimitReached);

    private void RefuseOn(StubSource source)
    {
        var served = _fixture.Client.Answer;
        _fixture.Client.Answer = (request, token) => request.ItemId == source.ItemId ? Task.FromResult(OverLimit()) : served(request, token);
    }

    [Fact]
    public async Task Over_the_limit_the_next_source_serves()
    {
        var full = PlaybackValues.Source("full");
        var next = PlaybackValues.Source("next");
        RefuseOn(full);

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(full, next));

        Assert.Equal(PlaybackOutcomeKind.Remote, outcome.Kind);
        Assert.Equal(next, Assert.Single(_fixture.Sessions.All).Source);
    }

    [Fact]
    public async Task The_source_at_its_limit_is_not_asked_for_its_file()
    {
        var full = PlaybackValues.Source("full");
        RefuseOn(full);

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(full));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task The_local_fallback_reads_a_source_with_a_free_slot()
    {
        var full = PlaybackValues.Source("full");
        var free = PlaybackValues.Source("free");
        RefuseOn(full);
        _fixture.Client.Playlists = _ => RemoteFailure.ServerError;

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(full, free));

        Assert.Equal(PlaybackOutcomeKind.LocalTranscode, outcome.Kind);
        Assert.Equal(free, outcome.FileSource);
    }

    [Fact]
    public async Task Every_source_at_its_limit_is_told_apart()
    {
        var first = PlaybackValues.Source("first");
        var second = PlaybackValues.Source("second");
        RefuseOn(first);
        RefuseOn(second);

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(first, second));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.True(outcome.AtStreamLimit);
    }

    [Fact]
    public async Task One_source_at_its_limit_and_one_offline_is_not_a_limit()
    {
        var full = PlaybackValues.Source("full");
        var offline = PlaybackValues.Source("offline");
        RefuseOn(full);
        _fixture.Registry.Offline.Add(offline.ServerId);

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(full, offline));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.False(outcome.AtStreamLimit);
    }

    [Fact]
    public async Task One_source_at_its_limit_and_one_broken_is_not_a_limit()
    {
        var full = PlaybackValues.Source("full");
        var broken = PlaybackValues.Source("broken");
        RefuseOn(full);
        _fixture.Client.Playlists = _ => RemoteFailure.ServerError;
        _fixture.Client.Open = (_, _) => Task.FromResult(RemoteResult.Fail<RemoteContent>(RemoteFailure.ServerError));

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(full, broken));

        Assert.Equal(PlaybackOutcomeKind.Unavailable, outcome.Kind);
        Assert.False(outcome.AtStreamLimit);
    }

    [Fact]
    public async Task All_offline_is_not_a_limit()
    {
        var offline = PlaybackValues.Source("offline");
        _fixture.Registry.Offline.Add(offline.ServerId);

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(offline));

        Assert.False(outcome.AtStreamLimit);
    }

    [Fact]
    public void The_limit_text_names_no_server_and_is_not_the_offline_one()
    {
        Assert.Equal("Limite di stream raggiunto sul server di origine. Riprova più tardi.", PlaybackRefusal.LimitText);
        Assert.NotEqual(PlaybackRefusal.Text, PlaybackRefusal.LimitText);
    }

    [Fact]
    public async Task Closing_a_session_gives_the_slot_back_on_a_jellymesh_remote()
    {
        var source = PlaybackValues.Source();
        _fixture.Registry.AddJellyMesh(source.ServerId);
        await _fixture.OpenAsync(PlaybackValues.Stub(source));
        var session = Assert.Single(_fixture.Sessions.All);

        await _fixture.Coordinator.CloseAsync(session.Id);

        Assert.Equal([session.RemotePlaySessionId!], _fixture.Client.Released);
    }

    [Fact]
    public async Task A_direct_session_has_no_transcode_to_stop_but_still_gives_its_slot_back()
    {
        var source = PlaybackValues.Source();
        _fixture.Registry.AddJellyMesh(source.ServerId);
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        await _fixture.OpenAsync(PlaybackValues.Stub(source));
        var session = Assert.Single(_fixture.Sessions.All);

        await _fixture.Coordinator.CloseAsync(session.Id);

        Assert.Empty(_fixture.Client.Stopped);
        Assert.Single(_fixture.Client.Released);
    }

    [Fact]
    public async Task A_direct_read_of_a_jellymesh_remote_names_its_play_session()
    {
        var source = PlaybackValues.Source();
        _fixture.Registry.AddJellyMesh(source.ServerId);
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));

        await _fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.All(_fixture.Client.Opened, path => Assert.Contains("PlaySessionId=" + PlaybackValues.RemotePlaySession, path, StringComparison.Ordinal));
        Assert.Contains("PlaySessionId=", Assert.Single(_fixture.Sessions.All).UpstreamPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stock_remote_sees_none_of_this()
    {
        var source = PlaybackValues.Source();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        await _fixture.OpenAsync(PlaybackValues.Stub(source));
        var session = Assert.Single(_fixture.Sessions.All);

        await _fixture.Coordinator.CloseAsync(session.Id);

        Assert.Empty(_fixture.Client.Released);
        Assert.DoesNotContain("PlaySessionId", session.UpstreamPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_that_cannot_serve_gives_its_slot_back_at_once()
    {
        var source = PlaybackValues.Source();
        _fixture.Registry.AddJellyMesh(source.ServerId);
        _fixture.Client.Playlists = _ => RemoteFailure.ServerError;

        await _fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.Equal([PlaybackValues.RemotePlaySession + source.ItemId], _fixture.Client.Released);
    }

    [Fact]
    public async Task A_parked_session_gives_its_slot_back()
    {
        var source = PlaybackValues.Source();
        _fixture.Registry.AddJellyMesh(source.ServerId);
        await _fixture.OpenAsync(PlaybackValues.Stub(source));

        _fixture.Clock.Advance(PlaybackTimeouts.Idle);
        await _fixture.Coordinator.SweepAsync();

        Assert.Single(_fixture.Client.Released);
        Assert.Single(_fixture.Sessions.All);
    }

    [Fact]
    public async Task The_client_never_learns_of_a_slot_the_session_stays_opaque()
    {
        var source = PlaybackValues.Source();
        _fixture.Registry.AddJellyMesh(source.ServerId);

        var outcome = await _fixture.OpenAsync(PlaybackValues.Stub(source));

        Assert.NotEqual(_fixture.Sessions.All[0].RemotePlaySessionId, outcome.Response!.PlaySessionId);
    }

    [Fact]
    public async Task A_new_playback_info_of_the_same_stream_takes_over_the_slot()
    {
        var source = PlaybackValues.Source();
        _fixture.Registry.AddJellyMesh(source.ServerId);
        var stub = PlaybackValues.Stub(source);
        await _fixture.OpenAsync(stub);
        var first = Assert.Single(_fixture.Sessions.All).RemotePlaySessionId;

        await _fixture.OpenAsync(stub);

        Assert.Null(_fixture.Client.Requests[0].ReplacesPlaySessionId);
        Assert.Equal(first, _fixture.Client.Requests[1].ReplacesPlaySessionId);
    }

    [Fact]
    public async Task A_stock_remote_is_never_told_of_a_replacement()
    {
        var stub = PlaybackValues.Stub(PlaybackValues.Source());
        await _fixture.OpenAsync(stub);

        await _fixture.OpenAsync(stub);

        Assert.All(_fixture.Client.Requests, request => Assert.Null(request.ReplacesPlaySessionId));
    }
}
