using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Answers every image request of a federated photo with the image of the remote, encoded again (row 509). It is anonymous
/// like the image of a local photo (row 451), and a signed-in user who does not see the photo gets a 404 (row 458).
/// </summary>
public sealed class PhotoImageFilter : IAsyncActionFilter, IDisposable
{
    private const string ImageController = "Image";

    private const string ImageMediaType = "image/jpeg";

    private const int WriteBytes = 64 * 1024;

    private readonly StubLookup _stubs;
    private readonly IAuthorizationContext _authorization;
    private readonly IRemoteServerRegistry _registry;
    private readonly IJellyfinRemoteClient _client;
    private readonly IPluginConfigurationStore _store;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _queueWait;
    private readonly TimeSpan _writeLimit;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _paused = new();
    private readonly SemaphoreSlim _inMemory = new(PlaybackLimits.PhotoImagesInMemory, PlaybackLimits.PhotoImagesInMemory);

    /// <summary>
    /// Initializes a new instance of the <see cref="PhotoImageFilter"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="registry">The remote servers.</param>
    /// <param name="client">The client of the remotes.</param>
    /// <param name="store">The configuration, for the time limit.</param>
    /// <param name="clock">The clock.</param>
    public PhotoImageFilter(StubLookup stubs, IAuthorizationContext authorization, IRemoteServerRegistry registry, IJellyfinRemoteClient client, IPluginConfigurationStore store, TimeProvider clock)
        : this(stubs, authorization, registry, client, store, clock, PlaybackLimits.PhotoImageQueueWait, PlaybackTimeouts.PhotoImageWrite)
    {
    }

    // Takes how long a read waits for a place and a client may take, so that tests can shorten them.
    internal PhotoImageFilter(
        StubLookup stubs,
        IAuthorizationContext authorization,
        IRemoteServerRegistry registry,
        IJellyfinRemoteClient client,
        IPluginConfigurationStore store,
        TimeProvider clock,
        TimeSpan queueWait,
        TimeSpan writeLimit)
    {
        _stubs = stubs;
        _authorization = authorization;
        _registry = registry;
        _client = client;
        _store = store;
        _clock = clock;
        _queueWait = queueWait;
        _writeLimit = writeLimit;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!IsImageRead(context) || StubLookup.ItemIdOf(context) is not { } itemId || !_stubs.IsPhotoStub(itemId))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var user = (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
        context.Result = user is null || _stubs.Sees(user, itemId)
            ? await ServeAsync(context, itemId).ConfigureAwait(false)
            : new NotFoundResult();
    }

    /// <inheritdoc />
    public void Dispose() => _inMemory.Dispose();

    // Reads the image from the server of the stub; a 404 for anything else, as Jellyfin answers for a missing image.
    internal async Task<IActionResult> ServeAsync(ActionExecutingContext context, Guid itemId)
    {
        if (!IsPrimary(context) || _stubs.FindFile(itemId) is not { Kind: StubKind.Photo } stub)
        {
            return new NotFoundResult();
        }

        var size = SizeOf(context);
        var http = context.HttpContext;
        foreach (var source in stub.Sources.Where(source => stub.ServerId is not { } server || source.ServerId == server))
        {
            using var image = await ReadAsync(source, size, http.RequestAborted).ConfigureAwait(false);
            if (image is not null)
            {
                await WriteAsync(http, image.Content).ConfigureAwait(false);
                return new EmptyResult();
            }
        }

        return new NotFoundResult();
    }

    // Any read that names an item and a type of image, whatever its action, so that none is left to stock.
    private static bool IsImageRead(ActionExecutingContext context)
    {
        var method = context.HttpContext.Request.Method;
        return StubLookup.ActionOf(context).Controller == ImageController
            && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            && context.ActionArguments.TryGetValue("imageType", out var type)
            && type is ImageType;
    }

    // The Primary is the only image of a photo stub.
    private static bool IsPrimary(ActionExecutingContext context)
        => context.ActionArguments["imageType"] is ImageType.Primary && NumberOf(context, "imageIndex") is null or 0;

    private static RemotePhotoSize SizeOf(ActionExecutingContext context) => new()
    {
        MaxWidth = NumberOf(context, "maxWidth"),
        MaxHeight = NumberOf(context, "maxHeight"),
        Width = NumberOf(context, "width"),
        Height = NumberOf(context, "height"),
        FillWidth = NumberOf(context, "fillWidth"),
        FillHeight = NumberOf(context, "fillHeight"),
        Quality = NumberOf(context, "quality")
    };

    private static int? NumberOf(ActionExecutingContext context, string name)
        => context.ActionArguments.TryGetValue(name, out var value) ? value as int? : null;

    // Writes the image in pieces the server keeps in memory no longer than the client takes, and drops a client too slow.
    private async Task WriteAsync(HttpContext http, ReadOnlyMemory<byte> content)
    {
        http.Response.ContentType = ImageMediaType;
        http.Response.ContentLength = content.Length;
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentDisposition = "attachment";
        http.Response.Headers.CacheControl = "private, no-cache";
        if (HttpMethods.IsHead(http.Request.Method))
        {
            return;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        limit.CancelAfter(_writeLimit);
        try
        {
            for (var sent = 0; sent < content.Length; sent += WriteBytes)
            {
                await http.Response.Body.WriteAsync(content.Slice(sent, Math.Min(WriteBytes, content.Length - sent)), limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        {
            http.Abort();
        }
    }

    // Anyone can ask for an image, so the reads have places of their own and never take one of playback. The image also
    // holds a place in memory, until it is disposed.
    private async Task<HeldImage?> ReadAsync(StubSource source, RemotePhotoSize size, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(source.ServerId, _ => new SemaphoreSlim(PlaybackLimits.PhotoImagesPerServer, PlaybackLimits.PhotoImagesPerServer));
        if (!await gate.WaitAsync(_queueWait, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            if (IsPaused(source.ServerId) || !await _inMemory.WaitAsync(_queueWait, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            HeldImage? held = null;
            try
            {
                var read = await CallAsync(source, size, cancellationToken).ConfigureAwait(false);
                if (read.IsSuccess && PhotoThumbnails.IsCleanJpeg(read.Value!.Content.Span, JellyfinRemoteClient.PhotoImageSide))
                {
                    held = new HeldImage(read.Value.Content, _inMemory);
                }
                else if (read.Failure is RemoteFailure.Unreachable or RemoteFailure.Timeout or RemoteFailure.AuthenticationFailed)
                {
                    _paused[source.ServerId] = _clock.GetUtcNow() + PlaybackTimeouts.PhotoServerPause;
                }

                return held;
            }
            finally
            {
                if (held is null)
                {
                    _inMemory.Release();
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private bool IsPaused(Guid serverId) => _paused.TryGetValue(serverId, out var until) && _clock.GetUtcNow() < until;

    private async Task<RemoteResult<RemoteImage>> CallAsync(StubSource source, RemotePhotoSize size, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_store.Read(PlaybackTimes.From).StreamOpen);
        try
        {
            return await _registry.RunPlaybackCallAsync(
                source.ServerId,
                (endpoint, session, token) => _client.GetPhotoImageAsync(endpoint, session, source.ItemId, size, token),
                limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteResult.Fail<RemoteImage>(RemoteFailure.Timeout);
        }
    }

    // An image with its place in memory.
    private sealed class HeldImage : IDisposable
    {
        private readonly SemaphoreSlim _places;

        public HeldImage(ReadOnlyMemory<byte> content, SemaphoreSlim places)
        {
            Content = content;
            _places = places;
        }

        public ReadOnlyMemory<byte> Content { get; }

        public void Dispose() => _places.Release();
    }
}
