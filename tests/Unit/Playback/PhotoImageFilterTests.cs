using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

// Every image of a federated photo comes from the remote (row 509), anonymous like a local one (row 451).
public class PhotoImageFilterTests
{
    private const string PhotoPath = "/federata/server/Video domestici/Trips/IMG_0001.jpg";
    private static readonly Guid _itemId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _serverId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly byte[] _jpeg = PhotoPlaceholders.For("jpg");

    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private readonly Mock<IJellyfinRemoteClient> _client = new();
    private readonly FakeRegistry _registry = new();
    private readonly ManualClock _clock = new();
    private readonly InMemoryConfigurationStore _store = new();
    private readonly User _user = new("viewer", "auth", "reset");
    private readonly List<(string ItemId, RemotePhotoSize Size)> _asked = [];
    private Func<CancellationToken, Task<RemoteResult<RemoteImage>>> _answer;

    public PhotoImageFilterTests()
    {
        _store.Configuration.FederatedFolderPath = "/federata";
        _library.Setup(library => library.GetItemById(_itemId)).Returns(new Photo { Path = PhotoPath });
        _library.Setup(library => library.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([new Folder()]);
        _stubs.Setup(stubs => stubs.FindByPath(PhotoPath)).Returns(Stub());
        SignedOut();
        _answer = _ => Task.FromResult(Image(_jpeg));
        _client
            .Setup(client => client.GetPhotoImageAsync(It.IsAny<RemoteEndpoint>(), It.IsAny<RemoteSession>(), It.IsAny<string>(), It.IsAny<RemotePhotoSize>(), It.IsAny<CancellationToken>()))
            .Returns((RemoteEndpoint _, RemoteSession _, string itemId, RemotePhotoSize size, CancellationToken token) =>
            {
                lock (_asked)
                {
                    _asked.Add((itemId, size));
                }

                return _answer(token);
            });
    }

    private static StubEntry Stub(StubKind kind = StubKind.Photo) => new()
    {
        Id = "stub",
        Kind = kind,
        ServerId = _serverId,
        RelativePath = "server/Video domestici/Trips/IMG_0001.jpg",
        Sources = [new StubSource(Guid.NewGuid(), "other-server-item", "other"), new StubSource(_serverId, "remote-item", "ms")],
        IdentityKey = "key"
    };

    private static RemoteResult<RemoteImage> Image(byte[] bytes) => RemoteResult.Success(new RemoteImage(bytes, "image/jpeg"));

    private void SignedIn(bool sees)
    {
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(new AuthorizationInfo { User = _user, Token = PlaybackValues.LocalToken });
        if (sees)
        {
            _library.Setup(library => library.GetItemById<BaseItem>(_itemId, _user)).Returns(new Photo { Path = PhotoPath });
        }
    }

    private void SignedOut()
        => _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(new AuthorizationInfo());

    private PhotoImageFilter Filter(TimeSpan? queueWait = null, TimeSpan? writeLimit = null)
    {
        var lookup = new StubLookup(_library.Object, _stubs.Object, _store);
        return new PhotoImageFilter(lookup, _authorization.Object, _registry, _client.Object, _store, _clock, queueWait ?? TimeSpan.FromSeconds(30), writeLimit ?? TimeSpan.FromSeconds(30));
    }

    private static ActionExecutingContext Request(string action = "GetItemImage", string method = "GET", string controller = "Image", params (string Key, object? Value)[] arguments)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Response.Body = new MemoryStream();
        var values = new Dictionary<string, object?> { ["itemId"] = _itemId, ["imageType"] = ImageType.Primary };
        foreach (var (key, value) in arguments)
        {
            values[key] = value;
        }

        var descriptor = new ControllerActionDescriptor { ControllerName = controller, ActionName = action };
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [], values, new object());
    }

    private static async Task<bool> RunAsync(PhotoImageFilter filter, ActionExecutingContext context)
    {
        var passed = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            passed = true;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });
        return passed;
    }

    private static void AssertServed(ActionExecutingContext context)
    {
        Assert.IsType<EmptyResult>(context.Result);
        Assert.Equal("image/jpeg", context.HttpContext.Response.ContentType);
        Assert.Equal(_jpeg, BodyOf(context));
    }

    private static byte[] BodyOf(ActionExecutingContext context) => ((MemoryStream)context.HttpContext.Response.Body).ToArray();

    private static void AssertMissing(ActionExecutingContext context)
    {
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result, exactMatch: false).StatusCode);
        Assert.Empty(context.HttpContext.Response.Headers);
    }

    [Theory]
    [InlineData("GetItemImage", "GET")]
    [InlineData("GetItemImageByIndex", "GET")]
    [InlineData("GetItemImage2", "GET")]
    [InlineData("AnImageReadOfALaterVersion", "GET")]
    public async Task An_image_of_a_federated_photo_comes_from_the_remote_without_login(string action, string method)
    {
        var context = Request(action, method);

        Assert.False(await RunAsync(Filter(), context));

        AssertServed(context);
        Assert.Equal("remote-item", Assert.Single(_asked).ItemId);
    }

    [Fact]
    public async Task A_head_request_has_the_length_and_no_body()
    {
        var context = Request(method: "HEAD");

        Assert.False(await RunAsync(Filter(), context));

        Assert.IsType<EmptyResult>(context.Result);
        Assert.Equal(_jpeg.Length, context.HttpContext.Response.ContentLength);
        Assert.Empty(BodyOf(context));
    }

    [Fact]
    public async Task The_answer_is_no_sniffed_page_and_no_shared_cache_keeps_it()
    {
        var context = Request();

        await RunAsync(Filter(), context);

        var headers = context.HttpContext.Response.Headers;
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal("attachment", headers.ContentDisposition.ToString());
        Assert.Equal("private, no-cache", headers.CacheControl.ToString());
        Assert.Equal(["Cache-Control", "Content-Disposition", "Content-Length", "Content-Type", "X-Content-Type-Options"], headers.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_user_who_sees_the_photo_gets_the_image()
    {
        SignedIn(sees: true);
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        AssertServed(context);
    }

    // Row 458: as the stock route, which hides from a signed-in user what they do not see.
    [Fact]
    public async Task A_user_who_does_not_see_the_photo_gets_a_404_and_nothing_reaches_the_remote()
    {
        SignedIn(sees: false);
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        AssertMissing(context);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Only_the_size_of_the_request_reaches_the_remote()
    {
        var context = Request(arguments:
        [
            ("maxWidth", 1),
            ("maxHeight", 2),
            ("width", 3),
            ("height", 4),
            ("fillWidth", 5),
            ("fillHeight", 6),
            ("quality", 70),
            ("tag", "tag"),
            ("format", ImageFormat.Webp),
            ("foregroundLayer", "layer"),
            ("blur", 10)
        ]);
        context.HttpContext.Request.QueryString = new QueryString("?ApiKey=" + PlaybackValues.LocalToken);

        await RunAsync(Filter(), context);

        Assert.Equal(
            new RemotePhotoSize { MaxWidth = 1, MaxHeight = 2, Width = 3, Height = 4, FillWidth = 5, FillHeight = 6 },
            Assert.Single(_asked).Size);
    }

    [Fact]
    public async Task Without_sizes_none_is_asked()
    {
        await RunAsync(Filter(), Request());

        Assert.Equal(new RemotePhotoSize(), Assert.Single(_asked).Size);
    }

    [Theory]
    [InlineData(RemoteFailure.Unreachable)]
    [InlineData(RemoteFailure.Timeout)]
    [InlineData(RemoteFailure.Suspended)]
    [InlineData(RemoteFailure.UnknownServer)]
    public async Task With_the_remote_offline_the_image_is_missing(RemoteFailure failure)
    {
        _registry.Offline.Add(_serverId);
        _registry.OfflineFailure = failure;
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        AssertMissing(context);
    }

    [Fact]
    public async Task A_remote_that_fails_gives_a_missing_image()
    {
        _answer = _ => Task.FromResult(RemoteResult.Fail<RemoteImage>(RemoteFailure.ServerError));
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        AssertMissing(context);
    }

    [Fact]
    public async Task An_image_the_remote_did_not_encode_again_is_missing()
    {
        byte[] exif = [0xFF, 0xE1, 0x00, 0x08, .. Encoding.ASCII.GetBytes("Exif\0\0")];
        foreach (var hostile in new byte[][] { [.. _jpeg[..2], .. exif, .. _jpeg[2..]], PhotoPlaceholders.For("png"), _jpeg[..^2], [], Encoding.ASCII.GetBytes("<html></html>"), Widened() })
        {
            _answer = _ => Task.FromResult(Image(hostile));
            var context = Request();

            Assert.False(await RunAsync(Filter(), context));

            AssertMissing(context);
        }
    }

    // The placeholder JPEG with a frame 4097 pixels wide.
    private static byte[] Widened()
    {
        var bytes = _jpeg.ToArray();
        var frame = Enumerable.Range(2, bytes.Length - 10).First(i => bytes[i] == 0xFF && bytes[i + 1] is 0xC0 or 0xC2);
        bytes[frame + 7] = 0x10;
        bytes[frame + 8] = 0x01;
        return bytes;
    }

    // A placeholder only has a Primary: another image is missing here as on stock, and costs the remote no call.
    [Theory]
    [InlineData(ImageType.Backdrop, null)]
    [InlineData(ImageType.Thumb, 0)]
    [InlineData(ImageType.Primary, 1)]
    [InlineData(ImageType.Primary, -1)]
    public async Task An_image_a_photo_never_has_is_missing_without_a_remote_call(ImageType type, int? index)
    {
        var context = Request(arguments: [("imageType", type), ("imageIndex", index)]);

        Assert.False(await RunAsync(Filter(), context));

        AssertMissing(context);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task The_first_index_of_the_Primary_is_the_photo()
    {
        var context = Request("GetItemImageByIndex", arguments: ("imageIndex", 0));

        Assert.False(await RunAsync(Filter(), context));

        AssertServed(context);
    }

    [Fact]
    public async Task A_placeholder_the_registry_does_not_know_is_missing_and_never_stock()
    {
        _stubs.Setup(stubs => stubs.FindByPath(PhotoPath)).Returns((StubEntry?)null);
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        AssertMissing(context);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task A_stub_of_another_kind_at_the_path_is_missing()
    {
        _stubs.Setup(stubs => stubs.FindByPath(PhotoPath)).Returns(Stub(StubKind.Book));
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        AssertMissing(context);
        Assert.Empty(_asked);
    }

    // While the folder is unknown the placeholder is told by its thumbnail, which stock would otherwise serve.
    [Theory]
    [InlineData("")]
    [InlineData("/elsewhere")]
    public async Task A_photo_with_a_thumbnail_of_the_plugin_is_never_stock(string root)
    {
        _store.Configuration.FederatedFolderPath = root;
        var photo = new Photo { Path = PhotoPath };
        photo.AddImage(new ItemImageInfo { Path = "/federata/.jellymesh-thumbs/stub.jpg", Type = ImageType.Primary });
        _library.Setup(library => library.GetItemById(_itemId)).Returns(photo);
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        AssertMissing(context);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Local_photos_books_albums_and_unknown_items_stay_stock()
    {
        foreach (var item in new BaseItem?[]
        {
            new Photo { Path = "/media/foto/IMG_0001.jpg" },
            new Book { Path = "/federata/server/Libri/Dune.epub" },
            new PhotoAlbum { Path = "/federata/server/Video domestici/Trips" },
            null
        })
        {
            _library.Setup(library => library.GetItemById(_itemId)).Returns(item!);

            Assert.True(await RunAsync(Filter(), Request()));
        }

        Assert.Empty(_asked);
    }

    [Theory]
    [InlineData("SetItemImage", "POST", "Image")]
    [InlineData("DeleteItemImage", "DELETE", "Image")]
    [InlineData("UpdateItemImageIndex", "POST", "Image")]
    [InlineData("GetItemImage", "GET", "RemoteImage")]
    public async Task Edits_of_the_image_and_other_controllers_stay_stock(string action, string method, string controller)
    {
        Assert.True(await RunAsync(Filter(), Request(action, method, controller)));
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task The_list_of_the_images_of_an_item_stays_stock()
    {
        var context = Request("GetItemImageInfos");
        context.ActionArguments.Remove("imageType");

        Assert.True(await RunAsync(Filter(), context));
    }

    // The reads only take places of their own, and one over them waits. Anonymous ones leave some to users (row 540).
    [Theory]
    [InlineData(true, PlaybackLimits.PhotoImagesPerServer)]
    [InlineData(false, PlaybackLimits.AnonymousPhotoImagesPerServer)]
    public async Task Reads_over_the_limit_wait_and_then_are_missing(bool signedIn, int places)
    {
        if (signedIn)
        {
            SignedIn(sees: true);
        }

        var release = new TaskCompletionSource();
        _answer = async _ =>
        {
            await release.Task;
            return Image(_jpeg);
        };
        var filter = Filter(TimeSpan.FromMilliseconds(50));
        var running = Enumerable.Range(0, places).Select(_ => Request()).ToList();
        var tasks = running.Select(request => RunAsync(filter, request)).ToList();
        var over = Request();

        await RunAsync(filter, over).WaitAsync(TimeSpan.FromSeconds(30));
        AssertMissing(over);
        Assert.Equal(places, _asked.Count);

        // A read that gave up waiting keeps no place.
        await Task.WhenAll(Enumerable.Range(0, PlaybackLimits.PhotoImagesInMemory).Select(_ => RunAsync(filter, Request()))).WaitAsync(TimeSpan.FromSeconds(30));
        release.SetResult();
        await Task.WhenAll(tasks);
        Assert.All(running, AssertServed);
        var after = Request();
        await RunAsync(filter, after);
        AssertServed(after);
    }

    // An anonymous client that reads slowly must not pile images up in memory, and is dropped in the end.
    [Fact]
    public async Task Images_not_yet_read_by_their_clients_are_bounded_and_dropped_after_the_limit()
    {
        var filter = Filter(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(3));
        var slow = Enumerable.Range(0, AnonymousPlaces).Select(_ => SlowRequest()).ToList();
        var tasks = new List<Task<bool>>();
        foreach (var (context, _) in slow)
        {
            tasks.Add(RunAsync(filter, context));
        }

        Assert.All(slow, request => Assert.False(request.Lifetime.Aborted));
        var over = Request();
        await RunAsync(filter, over).WaitAsync(TimeSpan.FromSeconds(30));
        AssertMissing(over);

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(slow, request => Assert.True(request.Lifetime.Aborted));
        var after = Request();
        await RunAsync(filter, after);
        AssertServed(after);
    }

    // Row 534: however many anonymous clients hold an image or wait for one, some places stay for signed-in users.
    [Fact]
    public async Task Anonymous_images_leave_some_places_to_signed_in_users()
    {
        using var gone = new CancellationTokenSource();
        var filter = Filter();
        var slow = Enumerable.Range(0, AnonymousPlaces).Select(_ => RunAsync(filter, SlowRequest(gone.Token).Context)).ToList();
        var waiting = Enumerable.Range(0, PlaybackLimits.PhotoImagesPerServer * 2).Select(_ => Request()).ToList();
        var waits = waiting.Select(request => RunAsync(filter, request)).ToList();
        Assert.Equal(AnonymousPlaces, _asked.Count);

        SignedIn(sees: true);
        var signedIn = Request();
        await RunAsync(filter, signedIn).WaitAsync(TimeSpan.FromSeconds(20));
        AssertServed(signedIn);
        Assert.All(waits, wait => Assert.False(wait.IsCompleted));

        await gone.CancelAsync();
        await Task.WhenAll(slow.Concat(waits)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(waiting, AssertServed);
    }

    // Row 540: anonymous reads that wait for a slow server leave signed-in users some of its places.
    [Fact]
    public async Task Anonymous_reads_of_a_slow_server_leave_places_to_signed_in_users()
    {
        var release = new TaskCompletionSource();
        var filter = Filter();
        _answer = async _ =>
        {
            await release.Task;
            return Image(_jpeg);
        };
        var anonymous = Enumerable.Range(0, PlaybackLimits.PhotoImagesInMemory).Select(_ => Request()).ToList();
        var reads = anonymous.Select(request => RunAsync(filter, request)).ToList();
        Assert.Equal(PlaybackLimits.AnonymousPhotoImagesPerServer, _asked.Count);

        SignedIn(sees: true);
        var waiting = Enumerable.Range(0, PlaybackLimits.PhotoImagesPerServer - PlaybackLimits.AnonymousPhotoImagesPerServer).Select(_ => Request()).ToList();
        var started = waiting.Select(request => RunAsync(filter, request)).ToList();
        Assert.Equal(PlaybackLimits.PhotoImagesPerServer, _asked.Count);
        Assert.All(reads, read => Assert.False(read.IsCompleted));

        release.SetResult();
        await Task.WhenAll(reads.Concat(started)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(anonymous.Concat(waiting), AssertServed);
    }

    // Nothing is kept from signed-in users, and an anonymous read that finds no place keeps none.
    [Fact]
    public async Task Signed_in_users_can_hold_every_place()
    {
        using var gone = new CancellationTokenSource();
        var filter = Filter(TimeSpan.FromMilliseconds(50));
        SignedIn(sees: true);
        var slow = Enumerable.Range(0, PlaybackLimits.PhotoImagesInMemory).Select(_ => RunAsync(filter, SlowRequest(gone.Token).Context)).ToList();
        Assert.Equal(PlaybackLimits.PhotoImagesInMemory, _asked.Count);

        SignedOut();
        var over = Enumerable.Range(0, PlaybackLimits.PhotoImagesInMemory).Select(_ => Request()).ToList();
        await Task.WhenAll(over.Select(request => RunAsync(filter, request))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(over, AssertMissing);

        await gone.CancelAsync();
        await Task.WhenAll(slow).WaitAsync(TimeSpan.FromSeconds(30));
        var after = Request();
        await RunAsync(filter, after);
        AssertServed(after);
    }

    // A client that goes away while its read waits for a place keeps none.
    [Fact]
    public async Task A_client_that_goes_away_while_it_waits_keeps_no_place()
    {
        using var gone = new CancellationTokenSource();
        using var left = new CancellationTokenSource();
        var filter = Filter();
        var slow = Enumerable.Range(0, AnonymousPlaces).Select(_ => RunAsync(filter, SlowRequest(gone.Token).Context)).ToList();
        var waits = Enumerable.Range(0, AnonymousPlaces)
            .Select(_ => SlowRequest(left.Token))
            .Select(request => (request.Context, request.Lifetime, Run: RunAsync(filter, request.Context)))
            .ToList();

        await left.CancelAsync();
        await Task.WhenAll(waits.Select(wait => wait.Run)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(waits, wait => Assert.IsType<EmptyResult>(wait.Context.Result));
        Assert.All(waits, wait => Assert.True(wait.Lifetime.Aborted));

        Assert.Equal(AnonymousPlaces, _asked.Count);
        await gone.CancelAsync();
        await Task.WhenAll(slow).WaitAsync(TimeSpan.FromSeconds(30));
        var after = Request();
        await RunAsync(filter, after);
        AssertServed(after);
    }

    private static int AnonymousPlaces => PlaybackLimits.PhotoImagesInMemory - PlaybackLimits.PhotoImagesForUsers;

    private static (ActionExecutingContext Context, AbortRecord Lifetime) SlowRequest(CancellationToken aborted = default)
    {
        var context = Request();
        var lifetime = new AbortRecord { RequestAborted = aborted };
        context.HttpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.HttpContext.Response.Body = new UnreadStream();
        return (context, lifetime);
    }

    private sealed class AbortRecord : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }

    // A client that never reads: a write waits until it is cancelled.
    private sealed class UnreadStream : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    // A client that closes its connection during the write ends the request quietly, and its place is free again.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_client_that_goes_away_ends_the_request(bool cancelled)
    {
        var filter = Filter();
        for (var turn = 0; turn <= PlaybackLimits.PhotoImagesInMemory; turn++)
        {
            var (context, lifetime) = SlowRequest();
            using var gone = new CancellationTokenSource();
            lifetime.RequestAborted = gone.Token;
            context.HttpContext.Response.Body = new GoneStream(cancelled ? gone : null);
            await RunAsync(filter, context).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(lifetime.Aborted);
        }

        var after = Request();
        await RunAsync(filter, after);
        AssertServed(after);
    }

    // A client that closes its connection at the first write, seen as a cancelled request or as a broken pipe.
    private sealed class GoneStream(CancellationTokenSource? gone) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (gone is null)
            {
                throw new IOException("Broken pipe");
            }

            await gone.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    // A server that does not answer or refuses the login is not asked again for each photo of a page.
    [Theory]
    [InlineData(RemoteFailure.Unreachable, 1)]
    [InlineData(RemoteFailure.Timeout, 1)]
    [InlineData(RemoteFailure.AuthenticationFailed, 1)]
    [InlineData(RemoteFailure.OtherServer, 1)]
    [InlineData(RemoteFailure.AdministratorAccount, 1)]
    [InlineData(RemoteFailure.ServerError, 2)]
    [InlineData(RemoteFailure.InvalidResponse, 2)]
    public async Task After_a_failure_of_the_server_its_images_pause(RemoteFailure failure, int calls)
    {
        var filter = Filter();
        _answer = _ => Task.FromResult(RemoteResult.Fail<RemoteImage>(failure));
        await RunAsync(filter, Request());
        var second = Request();
        await RunAsync(filter, second);
        AssertMissing(second);
        Assert.Equal(calls, _asked.Count);

        _clock.Advance(PlaybackTimeouts.PhotoServerPause);
        _answer = _ => Task.FromResult(Image(_jpeg));
        var third = Request();
        await RunAsync(filter, third);
        AssertServed(third);
    }

    // Row 534: an anonymous read leaves the state of the server alone, so it runs as a call that records nothing.
    [Fact]
    public async Task Only_an_image_without_a_user_is_read_as_an_anonymous_call()
    {
        await RunAsync(Filter(), Request());
        Assert.Equal(1, _registry.AnonymousCalls);

        SignedIn(sees: true);
        var context = Request();
        await RunAsync(Filter(), context);
        AssertServed(context);
        Assert.Equal(1, _registry.AnonymousCalls);
    }

    [Fact]
    public async Task A_failed_anonymous_read_pauses_no_signed_in_user()
    {
        var filter = Filter();
        _answer = _ => Task.FromResult(RemoteResult.Fail<RemoteImage>(RemoteFailure.Unreachable));
        await RunAsync(filter, Request());

        _answer = _ => Task.FromResult(Image(_jpeg));
        SignedIn(sees: true);
        var context = Request();
        await RunAsync(filter, context);
        AssertServed(context);

        SignedOut();
        var anonymous = Request();
        await RunAsync(filter, anonymous);
        AssertMissing(anonymous);
    }

    [Fact]
    public async Task A_failed_read_of_a_signed_in_user_pauses_the_anonymous_ones_too()
    {
        var filter = Filter();
        _answer = _ => Task.FromResult(RemoteResult.Fail<RemoteImage>(RemoteFailure.Unreachable));
        SignedIn(sees: true);
        await RunAsync(filter, Request());
        await RunAsync(filter, Request());
        Assert.Single(_asked);

        SignedOut();
        await RunAsync(filter, Request());
        Assert.Single(_asked);
    }

    [Fact]
    public async Task A_remote_that_hangs_is_missing_after_the_time_of_a_stream_opening()
    {
        _store.Configuration.StreamOpenTimeoutSeconds = 0;
        _answer = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Image(_jpeg);
        };
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        AssertMissing(context);
    }

    // Jellyfin logs an error for any exception, so a client that went ends its read without one, and pauses nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_client_that_went_ends_the_read(bool signedIn)
    {
        if (signedIn)
        {
            SignedIn(sees: true);
        }

        using var gone = new CancellationTokenSource();
        _answer = async token =>
        {
            await gone.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return Image(_jpeg);
        };
        var (context, lifetime) = SlowRequest(gone.Token);
        var filter = Filter();

        Assert.False(await RunAsync(filter, context));

        Assert.IsType<EmptyResult>(context.Result);
        Assert.True(lifetime.Aborted);
        _answer = _ => Task.FromResult(Image(_jpeg));
        var after = Request();
        await RunAsync(filter, after);
        AssertServed(after);
    }

    [Fact]
    public async Task A_client_that_went_during_the_authorization_ends_the_request()
    {
        using var gone = new CancellationTokenSource();
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .Returns(async () =>
            {
                await gone.CancelAsync();
                gone.Token.ThrowIfCancellationRequested();
                return new AuthorizationInfo();
            });
        var (context, _) = SlowRequest(gone.Token);

        Assert.False(await RunAsync(Filter(), context));

        Assert.IsType<EmptyResult>(context.Result);
        Assert.Empty(_asked);
    }

    // Only a client that went is answered with nothing: any other cancellation is still an error.
    [Fact]
    public async Task A_cancellation_of_a_client_still_there_is_an_error()
    {
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>())).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(Filter(), Request()));
    }
}
