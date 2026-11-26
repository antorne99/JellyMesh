using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

/// <summary>A track tells a JellyMesh server which device plays it, so that its streams share a slot (row 511).</summary>
public class TrackDeviceTests
{
    private readonly CoordinatorFixture _fixture = new();

    private static Guid User => PlaybackValues.Identity().UserId;

    private static string Device => PlaybackValues.Identity().DeviceId;

    private static StubEntry Track(StubSource source, string id = "stub") => PlaybackValues.Stub(source) with { Id = id, Kind = StubKind.Track, ServerId = source.ServerId };

    private StubSource JellyMeshSource(string item = "remote-item")
    {
        var source = PlaybackValues.Source(item);
        _fixture.Registry.AddJellyMesh(source.ServerId);
        return source;
    }

    private string HashFor(StubSource source, string? device = null) => _fixture.Hasher.Hash(source.ServerId, User, device ?? Device)!;

    private void PlaysDirect() => _fixture.Client.Answer = (request, _) => Task.FromResult(FakePlaybackClient.Direct(request));

    private Task<PlaybackOutcome> OpenAsync(StubEntry stub, string device)
    {
        var request = new PlaybackOpenRequest { Identity = PlaybackValues.Identity() with { DeviceId = device }, Stub = stub, LocalToken = PlaybackValues.LocalToken };
        return _fixture.Coordinator.OpenAsync(request, CancellationToken.None);
    }

    private async Task<bool> ReadFileAsync(StubEntry stub, string device, params StubEntry[] copies)
    {
        using var read = await _fixture.Coordinator.OpenTrackFileAsync(User, device, stub, copies, HttpMethod.Get, null, CancellationToken.None);
        return read is not null;
    }

    [Fact]
    public async Task Everything_a_transcoded_track_asks_names_its_device()
    {
        var source = JellyMeshSource();

        Assert.Equal(PlaybackOutcomeKind.Remote, (await _fixture.OpenAsync(Track(source))).Kind);

        Assert.Equal(HashFor(source), Assert.Single(_fixture.Client.Requests).Device);
        Assert.Equal([HashFor(source), HashFor(source), HashFor(source)], _fixture.Client.Devices);
        Assert.Equal(HashFor(source), Assert.Single(_fixture.Sessions.All).RemoteDevice);
    }

    [Fact]
    public async Task Everything_a_track_played_directly_asks_names_its_device()
    {
        var source = JellyMeshSource();
        PlaysDirect();

        Assert.Equal(PlaybackOutcomeKind.Remote, (await _fixture.OpenAsync(Track(source))).Kind);

        Assert.Equal(HashFor(source), Assert.Single(_fixture.Client.Requests).Device);
        Assert.Equal([HashFor(source)], _fixture.Client.Devices);
        Assert.Equal(HashFor(source), Assert.Single(_fixture.Sessions.All).RemoteDevice);
    }

    [Fact]
    public async Task A_track_transcoded_to_one_stream_names_its_device_when_it_is_tried()
    {
        var source = JellyMeshSource();
        _fixture.Client.Answer = (request, _) =>
        {
            var answer = FakePlaybackClient.Hls(request);
            answer.Value!.MediaSources[0].TranscodingSubProtocol = MediaStreamProtocol.http;
            return Task.FromResult(answer);
        };

        Assert.Equal(PlaybackOutcomeKind.Remote, (await _fixture.OpenAsync(Track(source))).Kind);

        Assert.Equal([HashFor(source)], _fixture.Client.Devices);
    }

    [Fact]
    public async Task What_the_client_reads_of_a_session_names_its_device()
    {
        var source = JellyMeshSource();
        var path = PlaybackUrls.WithoutToken(PlaybackValues.AudioMaster);
        var session = new PlaybackSession(PlaybackValues.Identity(), source, PlaybackValues.RemotePlaySession, PlaybackStreamKind.Hls, path, _fixture.Clock.GetUtcNow()) { RemoteDevice = HashFor(source) };
        var relay = new SessionRelay(_fixture.Coordinator, new PlaybackRefusal(Mock.Of<ISessionManager>(), NullLogger<PlaybackRefusal>.Instance), _fixture.Clock, NullLogger.Instance);
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";

        await relay.PlaylistAsync(http, session, path, session.UpstreamFolder(), string.Empty, PlaybackValues.LocalToken, SessionRelay.PlaylistType, CancellationToken.None);
        await relay.StreamAsync(http, session, "/Audio/remote-item/hls1/main/0.mp4", CancellationToken.None);

        Assert.Equal([HashFor(source), HashFor(source)], _fixture.Client.Devices);
    }

    [Fact]
    public async Task A_server_that_stopped_running_JellyMesh_is_told_of_no_device()
    {
        var source = JellyMeshSource();
        Assert.Equal(PlaybackOutcomeKind.Remote, (await _fixture.OpenAsync(Track(source))).Kind);
        var session = Assert.Single(_fixture.Sessions.All);
        _fixture.Client.Devices.Clear();
        _fixture.Registry.Servers.Clear();

        await _fixture.Coordinator.ReadPlaylistAsync(session, session.UpstreamPath, CancellationToken.None);
        using var read = (await _fixture.Coordinator.OpenAsync(source.ServerId, new RemoteStreamRequest(HttpMethod.Get, "/Audio/remote-item/hls1/main/0.mp4", null, Device: session.RemoteDevice), CancellationToken.None)).Value;

        Assert.Equal([null, null], _fixture.Client.Devices);
    }

    [Fact]
    public async Task The_hash_of_a_device_never_reaches_a_client()
    {
        var source = JellyMeshSource();

        Assert.Equal(PlaybackOutcomeKind.Remote, (await _fixture.OpenAsync(Track(source))).Kind);

        Assert.Contains(HashFor(source), PlaybackCoordinator.SecretsOf(Assert.Single(_fixture.Sessions.All)));
    }

    // One slot per device, whichever way a client reads its tracks.
    [Fact]
    public async Task A_file_read_names_the_same_device_as_a_playback_info()
    {
        var source = JellyMeshSource();

        await _fixture.OpenAsync(Track(source));
        var played = Assert.Single(_fixture.Client.Requests).Device;
        _fixture.Client.Devices.Clear();
        Assert.True(await ReadFileAsync(Track(source), Device));

        Assert.NotNull(played);
        Assert.Equal([played], _fixture.Client.Devices);
    }

    [Fact]
    public async Task Two_devices_of_a_user_are_told_apart()
    {
        var source = JellyMeshSource();

        await OpenAsync(Track(source), "phone");
        await OpenAsync(Track(source), "tablet");

        Assert.Equal([HashFor(source, "phone"), HashFor(source, "tablet")], _fixture.Client.Requests.Select(request => request.Device));
        Assert.NotEqual(_fixture.Client.Requests[0].Device, _fixture.Client.Requests[1].Device);
    }

    [Fact]
    public async Task A_copy_on_another_server_gets_the_hash_made_for_that_server()
    {
        var (track, copy) = (JellyMeshSource("a"), JellyMeshSource("b"));
        _fixture.Registry.Offline.Add(track.ServerId);

        Assert.True(await ReadFileAsync(Track(track), Device, Track(copy, "copy")));

        Assert.Equal([HashFor(copy)], _fixture.Client.Devices);
        Assert.NotEqual(HashFor(track), HashFor(copy));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stock_remote_is_never_told_of_a_device(bool direct)
    {
        var source = PlaybackValues.Source();
        if (direct)
        {
            PlaysDirect();
        }

        Assert.Equal(PlaybackOutcomeKind.Remote, (await _fixture.OpenAsync(Track(source))).Kind);
        Assert.True(await ReadFileAsync(Track(source), Device));

        Assert.Null(Assert.Single(_fixture.Client.Requests).Device);
        Assert.NotEmpty(_fixture.Client.Devices);
        Assert.All(_fixture.Client.Devices, Assert.Null);
        Assert.Null(Assert.Single(_fixture.Sessions.All).RemoteDevice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_film_names_no_device(bool direct)
    {
        var source = JellyMeshSource();
        if (direct)
        {
            PlaysDirect();
        }

        Assert.Equal(PlaybackOutcomeKind.Remote, (await _fixture.OpenAsync(PlaybackValues.Stub(source))).Kind);

        Assert.Null(Assert.Single(_fixture.Client.Requests).Device);
        Assert.NotEmpty(_fixture.Client.Devices);
        Assert.All(_fixture.Client.Devices, Assert.Null);
        Assert.Null(Assert.Single(_fixture.Sessions.All).RemoteDevice);
    }

    [Fact]
    public async Task An_access_with_no_device_names_none()
    {
        var source = JellyMeshSource();

        await OpenAsync(Track(source), string.Empty);
        Assert.True(await ReadFileAsync(Track(source), string.Empty));

        Assert.Null(Assert.Single(_fixture.Client.Requests).Device);
        Assert.NotEmpty(_fixture.Client.Devices);
        Assert.All(_fixture.Client.Devices, Assert.Null);
    }

    // Row 561: an audiobook shares the slot of its device as a track does, played or read.
    [Fact]
    public async Task An_audiobook_names_its_device_when_played_and_when_its_file_is_read()
    {
        var source = JellyMeshSource();
        var book = Track(source) with { Kind = StubKind.AudioBook };

        Assert.Equal(PlaybackOutcomeKind.Remote, (await _fixture.OpenAsync(book)).Kind);
        Assert.Equal(HashFor(source), Assert.Single(_fixture.Client.Requests).Device);
        _fixture.Client.Devices.Clear();
        Assert.True(await ReadFileAsync(book, Device));

        Assert.Equal([HashFor(source)], _fixture.Client.Devices);
        Assert.Equal(HashFor(source), Assert.Single(_fixture.Sessions.All).RemoteDevice);
    }

    // The .strm of a track and the file of a book have no device: they count as before.
    [Theory]
    [InlineData(StubKind.Track)]
    [InlineData(StubKind.Book)]
    [InlineData(StubKind.Movie)]
    public async Task A_file_read_by_no_device_names_none(StubKind kind)
    {
        var source = JellyMeshSource();

        using var read = await _fixture.Coordinator.OpenFileAsync(Track(source) with { Kind = kind }, HttpMethod.Get, null, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Null(Assert.Single(_fixture.Client.Devices));
    }
}
