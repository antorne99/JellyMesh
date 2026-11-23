using System;
using System.Collections.Generic;
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
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>())).ReturnsAsync(new AuthorizationInfo());
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

    private PhotoImageFilter Filter(TimeSpan? queueWait = null)
    {
        var lookup = new StubLookup(_library.Object, _stubs.Object, _store);
        return new PhotoImageFilter(lookup, _authorization.Object, _registry, _client.Object, _store, queueWait ?? TimeSpan.FromSeconds(30));
    }

    private static ActionExecutingContext Request(string action = "GetItemImage", string method = "GET", string controller = "Image", params (string Key, object? Value)[] arguments)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
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

    private static void AssertMissing(ActionExecutingContext context)
    {
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<StatusCodeResult>(context.Result, exactMatch: false).StatusCode);
    }

    [Theory]
    [InlineData("GetItemImage", "GET")]
    [InlineData("GetItemImage", "HEAD")]
    [InlineData("GetItemImageByIndex", "GET")]
    [InlineData("GetItemImage2", "GET")]
    [InlineData("AnImageReadOfALaterVersion", "GET")]
    public async Task An_image_of_a_federated_photo_comes_from_the_remote_without_login(string action, string method)
    {
        var context = Request(action, method);

        Assert.False(await RunAsync(Filter(), context));

        var result = Assert.IsType<FileContentResult>(context.Result);
        Assert.Equal(_jpeg, result.FileContents);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal("remote-item", Assert.Single(_asked).ItemId);
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
        Assert.Equal(3, headers.Count);
    }

    [Fact]
    public async Task A_user_who_sees_the_photo_gets_the_image()
    {
        SignedIn(sees: true);
        var context = Request();

        Assert.False(await RunAsync(Filter(), context));

        Assert.IsType<FileContentResult>(context.Result);
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
    public async Task Only_the_size_and_the_quality_of_the_request_reach_the_remote()
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
            new RemotePhotoSize { MaxWidth = 1, MaxHeight = 2, Width = 3, Height = 4, FillWidth = 5, FillHeight = 6, Quality = 70 },
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
        Assert.Empty(context.HttpContext.Response.Headers);
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

        Assert.IsType<FileContentResult>(context.Result);
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

    // The reads are anonymous, so they only take places of their own, and one over them waits.
    [Fact]
    public async Task Reads_over_the_limit_wait_and_then_are_missing()
    {
        var release = new TaskCompletionSource();
        _answer = async _ =>
        {
            await release.Task;
            return Image(_jpeg);
        };
        var filter = Filter(TimeSpan.FromMilliseconds(50));
        var running = Enumerable.Range(0, PlaybackLimits.PhotoImagesPerServer).Select(_ => Request()).ToList();
        var tasks = running.Select(request => RunAsync(filter, request)).ToList();
        var over = Request();

        await RunAsync(filter, over).WaitAsync(TimeSpan.FromSeconds(30));
        AssertMissing(over);
        Assert.Equal(PlaybackLimits.PhotoImagesPerServer, _asked.Count);

        release.SetResult();
        await Task.WhenAll(tasks);
        Assert.All(running, request => Assert.IsType<FileContentResult>(request.Result));
        var after = Request();
        await RunAsync(filter, after);
        Assert.IsType<FileContentResult>(after.Result);
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

    [Fact]
    public async Task A_client_that_went_ends_the_read()
    {
        using var gone = new CancellationTokenSource();
        _answer = async token =>
        {
            await gone.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return Image(_jpeg);
        };
        var context = Request();
        context.HttpContext.RequestAborted = gone.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(Filter(), context));
    }
}
