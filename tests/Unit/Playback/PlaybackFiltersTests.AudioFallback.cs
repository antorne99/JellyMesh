using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Segments;
using Jellyfin.Plugin.JellyMesh.Tests.Segments;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// Riproduzione audio: the fallback of a track to its copies on other servers (row 457, D65).
public partial class PlaybackFiltersTests
{
    private const string TrackKey = "Track|mbr:recording";
    private static readonly Guid _copyId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    // A track stub in the federated folder, as the planner writes it: one source, its server, and the dedup key.
    private static StubEntry TrackEntry(string id, StubSource source) => new()
    {
        Id = id,
        Kind = StubKind.Track,
        ServerId = source.ServerId,
        RelativePath = source.ServerId.ToString("D") + "/library/Musica/Artist/Album/" + id + ".strm",
        Sources = [source],
        IdentityKey = TrackKey
    };

    // The track at _itemId and its copy at _copyId on another server; the user sees the track, and the copy when asked.
    private (InMemoryConfigurationStore Store, StubEntry Track, StubEntry Copy) TrackWithCopy(bool seesCopy = true)
    {
        var store = new InMemoryConfigurationStore();
        store.Configuration.FederatedFolderPath = "/federata";
        var track = TrackEntry("track-a", PlaybackValues.Source("item-a"));
        var copy = TrackEntry("track-b", PlaybackValues.Source("item-b"));
        AddTrack(track, _itemId, true);
        AddTrack(copy, _copyId, seesCopy);
        _stubs.Setup(stubs => stubs.FindCopies(TrackKey)).Returns([track, copy]);
        return (store, track, copy);
    }

    private readonly List<ServerCatalog> _trackCatalogs = [];

    // The index the versions read the audio quality of the copies from.
    private IFederationIndex TrackIndex()
    {
        var index = new Mock<IFederationIndex>();
        index.Setup(federation => federation.Snapshot).Returns(() => new IndexSnapshot { Version = IndexSnapshot.CurrentVersion, Health = IndexHealth.Ok, Servers = _trackCatalogs.ToList() });
        return index.Object;
    }

    // A further copy of the track on a server of its own, with the codec and bitrate the index knows.
    private StubEntry AddCopy(string id, Guid itemId, string codec, int bitRate)
    {
        var copy = TrackEntry(id, PlaybackValues.Source("item-" + id));
        var source = copy.Sources[0];
        _trackCatalogs.Add(new ServerCatalog
        {
            ServerId = source.ServerId,
            State = ServerSyncState.Complete,
            Items =
            [
                new IndexedItem
                {
                    ItemId = source.ItemId,
                    Kind = IndexedItemKind.Audio,
                    LibraryId = "lib",
                    LastSeenUtc = DateTimeOffset.UnixEpoch,
                    Versions = [new IndexedVersion { MediaSourceId = source.MediaSourceId, Streams = [new IndexedTrack { Type = "Audio", Codec = codec, BitRate = bitRate, SampleRate = 44100 }] }]
                }
            ]
        });
        AddTrack(copy, itemId, true);
        return copy;
    }

    private void AddTrack(StubEntry stub, Guid itemId, bool sees)
    {
        var path = "/federata/" + stub.RelativePath;
        var item = new MediaBrowser.Controller.Entities.Audio.Audio { Id = itemId, Path = path };
        _library.Setup(library => library.GetItemById(itemId)).Returns(item);
        _library.Setup(library => library.FindByPath(path, false)).Returns(item);
        _stubs.Setup(stubs => stubs.FindByPath(path)).Returns(stub);
        _stubs.Setup(stubs => stubs.Find(stub.Id)).Returns(stub);
        if (sees)
        {
            _library.Setup(library => library.GetItemById<BaseItem>(itemId, _user)).Returns(item);
        }
    }

    [Fact]
    public async Task PlaybackInfo_of_a_track_whose_server_is_offline_plays_its_copy_on_another_server()
    {
        var (store, track, _) = TrackWithCopy();
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(store), context));
        var response = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(context.Result).Value);
        Assert.Equal(_copyId.ToString("N"), Assert.Single(response.MediaSources).Id);
        Assert.Equal("item-b", Assert.Single(_fixture.Client.Requests).ItemId);
    }

    // Row 65: a copy in a library the user does not see is never a fallback, so the track keeps its clear error.
    [Fact]
    public async Task PlaybackInfo_of_a_track_never_falls_back_to_a_copy_the_user_cannot_see()
    {
        var (store, track, _) = TrackWithCopy(seesCopy: false);
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(store), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Requests);
    }

    // Row 453: after the track asked, its copies go best first, lossless before lossy, whatever the registry order.
    [Fact]
    public async Task PlaybackInfo_of_a_track_tries_its_copies_best_first()
    {
        var store = new InMemoryConfigurationStore();
        store.Configuration.FederatedFolderPath = "/federata";
        var track = TrackEntry("track-a", PlaybackValues.Source("item-a"));
        AddTrack(track, _itemId, true);
        var lossy = AddCopy("lossy", Guid.NewGuid(), "mp3", 320000);
        var lossless = AddCopy("lossless", Guid.NewGuid(), "flac", 900000);
        _stubs.Setup(stubs => stubs.FindCopies(TrackKey)).Returns([track, lossy, lossless]);
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(store), context));
        Assert.Equal(["item-a", "item-lossless", "item-lossy"], _fixture.Client.Requests.Select(request => request.ItemId));
    }

    // The planner writes a track once per local library: those stubs read the same remote track, which is tried once.
    [Fact]
    public async Task PlaybackInfo_of_a_track_never_tries_the_same_remote_track_twice()
    {
        var (store, track, copy) = TrackWithCopy();
        var sameTrack = TrackEntry("track-a-other-library", track.Sources[0]) with { RelativePath = track.ServerId!.Value.ToString("D") + "/other/Musica/Artist/Album/a.strm" };
        AddTrack(sameTrack, Guid.NewGuid(), true);
        _stubs.Setup(stubs => stubs.FindCopies(TrackKey)).Returns([track, sameTrack, copy]);
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId));

        Assert.False(await RunAsync(InfoFilter(store), context));
        Assert.Equal(["item-a", "item-b"], _fixture.Client.Requests.Select(request => request.ItemId));
    }

    // After a fallback the answer names the copy, and the client asks for it again under the track (D22 for tracks).
    [Fact]
    public async Task PlaybackInfo_naming_a_copy_of_the_track_plays_that_copy()
    {
        var (store, _, _) = TrackWithCopy();
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId), ("mediaSourceId", _copyId.ToString("N")));

        Assert.False(await RunAsync(InfoFilter(store), context));
        var response = Assert.IsType<PlaybackInfoResponse>(Assert.IsType<OkObjectResult>(context.Result).Value);
        Assert.Equal(_copyId.ToString("N"), Assert.Single(response.MediaSources).Id);
        Assert.Equal("item-b", _fixture.Client.Requests[0].ItemId);
    }

    [Fact]
    public async Task Universal_of_a_track_whose_server_is_offline_plays_its_copy()
    {
        var (store, track, _) = TrackWithCopy();
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var context = Universal();

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.StartsWith("/Videos/item-b/stream?static=true", _fixture.Client.Opened[^1], StringComparison.Ordinal);
    }

    // jellyfin-web sends a HEAD before the GET: the second one reuses the session of the copy, not the dead server.
    [Fact]
    public async Task Universal_after_a_fallback_reuses_the_session_of_the_copy()
    {
        var (store, track, _) = TrackWithCopy();
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var guard = GuardFilter(store: store);

        Assert.False(await RunAsync(guard, Universal("HEAD")));
        Assert.False(await RunAsync(guard, Universal()));
        Assert.Equal("item-b", Assert.Single(_fixture.Client.Requests).ItemId);
    }

    [Fact]
    public async Task Universal_naming_a_copy_of_the_track_plays_that_copy()
    {
        var (store, _, _) = TrackWithCopy();
        RemoteServes();
        _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));
        var context = AudioRequest("UniversalAudio", "GetUniversalAudioStream", "GET", ("container", new[] { "flac" }), ("mediaSourceId", _copyId.ToString("N")));

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.Equal("item-b", _fixture.Client.Requests[0].ItemId);
    }

    [Fact]
    public async Task Universal_naming_another_track_is_refused()
    {
        var (store, _, copy) = TrackWithCopy();
        _stubs.Setup(stubs => stubs.FindByPath("/federata/" + copy.RelativePath)).Returns(copy with { IdentityKey = "Track|mbr:other" });
        var context = AudioRequest("UniversalAudio", "GetUniversalAudioStream", "GET", ("container", new[] { "flac" }), ("mediaSourceId", _copyId.ToString("N")));

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Requests);
    }

    // Finamp reads a track with File: it falls back to a copy when it starts reading, without a Range or from byte 0.
    [Theory]
    [InlineData(null)]
    [InlineData("bytes=0-")]
    public async Task File_of_a_track_whose_server_is_offline_reads_its_copy_from_the_start(string? range)
    {
        var (store, track, _) = TrackWithCopy();
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        RemoteServes();
        var context = FileRequest("GetFile", range);

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.StartsWith("/Videos/item-b/stream?static=true", Assert.Single(_fixture.Client.Opened), StringComparison.Ordinal);
    }

    // A Range in the middle of the file goes on in the file it began in: the bytes of another copy would not match.
    [Fact]
    public async Task File_of_a_track_never_continues_a_range_in_another_copy()
    {
        var (store, track, _) = TrackWithCopy();
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        RemoteServes();
        var context = FileRequest("GetFile", "bytes=500-");

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Opened);
    }

    // Once a copy took over the file, the Range reads that follow stay on it.
    [Fact]
    public async Task File_of_a_track_goes_on_in_the_copy_that_took_over()
    {
        var (store, track, _) = TrackWithCopy();
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        RemoteServes();
        var guard = GuardFilter(store: store);
        Assert.False(await RunAsync(guard, FileRequest("GetFile")));

        var context = FileRequest("GetFile", "bytes=500-");
        Assert.False(await RunAsync(guard, context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.All(_fixture.Client.Opened, path => Assert.StartsWith("/Videos/item-b/", path, StringComparison.Ordinal));
        Assert.Equal(2, _fixture.Client.Opened.Count);
    }

    // After PlaybackInfo named the copy, direct play reads its file under the track, and a seek stays on that copy.
    [Fact]
    public async Task A_static_stream_naming_a_copy_of_the_track_reads_the_file_of_that_copy()
    {
        var (store, _, _) = TrackWithCopy();
        RemoteServes();
        var context = AudioRequest("Audio", "GetAudioStream", "GET", ("static", true), ("mediaSourceId", _copyId.ToString("N")));
        context.HttpContext.Request.Headers.Range = "bytes=500-";

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.StartsWith("/Videos/item-b/stream?static=true", Assert.Single(_fixture.Client.Opened), StringComparison.Ordinal);
    }

    // Row 66 for tracks: a report naming a copy the user cannot see gets the answer of stock, with no effect.
    [Theory]
    [InlineData("ReportPlaybackProgress")]
    [InlineData("ReportPlaybackStopped")]
    [InlineData("OnPlaybackProgress")]
    public async Task A_playstate_report_naming_a_copy_the_user_cannot_see_is_answered_without_effect(string action)
    {
        var (store, _, _) = TrackWithCopy(seesCopy: false);
        var context = Context("Playstate", action, PlaystateOf(action, _itemId, _copyId));

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.IsType<NoContentResult>(context.Result);
    }

    [Fact]
    public async Task A_playstate_report_naming_a_copy_the_user_sees_goes_to_stock()
    {
        var (store, _, _) = TrackWithCopy();

        Assert.True(await RunAsync(GuardFilter(store: store), Context("Playstate", "ReportPlaybackProgress", PlaystateOf("ReportPlaybackProgress", _itemId, _copyId))));
    }

    // The arguments of a Playstate action naming a track and a version of it.
    private static (string Key, object? Value)[] PlaystateOf(string action, Guid item, Guid version) => action switch
    {
        "ReportPlaybackProgress" => [("playbackProgressInfo", new PlaybackProgressInfo { ItemId = item, MediaSourceId = version.ToString("N"), PlaySessionId = "play" })],
        "ReportPlaybackStopped" => [("playbackStopInfo", new PlaybackStopInfo { ItemId = item, MediaSourceId = version.ToString("N"), PlaySessionId = "play" })],
        _ => [("itemId", item), ("mediaSourceId", version.ToString("N")), ("playSessionId", "play")]
    };

    [Fact]
    public async Task Universal_of_a_track_never_falls_back_to_a_copy_the_user_cannot_see()
    {
        var (store, track, _) = TrackWithCopy(seesCopy: false);
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        RemoteServes();
        var context = Universal();

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.DoesNotContain(_fixture.Client.Requests, request => request.ItemId == "item-b");
        Assert.Empty(_fixture.Client.Opened);
    }

    [Fact]
    public async Task Universal_naming_a_copy_the_user_cannot_see_is_not_found()
    {
        var (store, _, _) = TrackWithCopy(seesCopy: false);
        var context = AudioRequest("UniversalAudio", "GetUniversalAudioStream", "GET", ("container", new[] { "flac" }), ("mediaSourceId", _copyId.ToString("N")));

        Assert.False(await RunAsync(GuardFilter(store: store), context));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Empty(_fixture.Client.Requests);
    }

    // A copy the user no longer sees is never read again, even in the middle of a file it took over.
    [Fact]
    public async Task File_of_a_track_never_goes_on_in_a_copy_the_user_no_longer_sees()
    {
        var (store, track, _) = TrackWithCopy();
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        RemoteServes();
        var guard = GuardFilter(store: store);
        Assert.False(await RunAsync(guard, FileRequest("GetFile")));
        _library.Setup(library => library.GetItemById<BaseItem>(_copyId, _user)).Returns((BaseItem?)null);

        var context = FileRequest("GetFile", "bytes=500-");
        Assert.False(await RunAsync(guard, context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Single(_fixture.Client.Opened);
    }

    // Another device of the user reads its own file: the copy that took over on the first one is not its.
    [Fact]
    public async Task File_of_a_track_on_another_device_goes_on_in_its_own_copy()
    {
        var (store, track, _) = TrackWithCopy();
        _fixture.Registry.Offline.Add(track.ServerId!.Value);
        RemoteServes();
        var guard = GuardFilter(store: store);
        Assert.False(await RunAsync(guard, FileRequest("GetFile")));
        _fixture.Registry.Offline.Remove(track.ServerId!.Value);
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = _user, Token = Token, DeviceId = "another-device" });

        var context = FileRequest("GetFile", "bytes=500-");
        Assert.False(await RunAsync(guard, context));
        Assert.Equal(_remoteBytes, BodyOf(context));
        Assert.StartsWith("/Videos/item-a/", _fixture.Client.Opened[^1], StringComparison.Ordinal);
    }

    // No local audio transcode: when every copy answers but none can serve, the track stays with a clear error.
    [Fact]
    public async Task PlaybackInfo_of_a_track_no_copy_can_serve_tries_each_copy_and_never_reads_a_file()
    {
        var (store, _, _) = TrackWithCopy();
        _fixture.Client.Answer = (_, _) => Task.FromResult(RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.ServerError));
        var context = Context("MediaInfo", "GetPostedPlaybackInfo", ("itemId", _itemId), ("playbackInfoDto", new { DeviceProfile = PlaybackValues.WebProfile }));

        Assert.False(await RunAsync(InfoFilter(store), context));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<StatusCodeResult>(context.Result).StatusCode);
        Assert.Equal(["item-a", "item-b"], _fixture.Client.Requests.Select(request => request.ItemId));
        Assert.Empty(_fixture.Client.Opened);
    }
}
