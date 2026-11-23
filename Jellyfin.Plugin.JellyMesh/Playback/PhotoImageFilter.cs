using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _anonymousGates = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _paused = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _anonymousPaused = new();
    private readonly SemaphoreSlim _inMemory = new(PlaybackLimits.PhotoImagesInMemory, PlaybackLimits.PhotoImagesInMemory);
    private readonly SemaphoreSlim _anonymous = new(
        PlaybackLimits.PhotoImagesInMemory - PlaybackLimits.PhotoImagesForUsers,
        PlaybackLimits.PhotoImagesInMemory - PlaybackLimits.PhotoImagesForUsers);

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
            ? await ServeAsync(context, itemId, anonymous: user is null).ConfigureAwait(false)
            : new NotFoundResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _inMemory.Dispose();
        _anonymous.Dispose();
    }

    // Reads the image from the server of the stub; a 404 for anything else, as Jellyfin answers for a missing image.
    internal async Task<IActionResult> ServeAsync(ActionExecutingContext context, Guid itemId, bool anonymous)
    {
        if (!IsPrimary(context) || _stubs.FindFile(itemId) is not { Kind: StubKind.Photo } stub)
        {
            return new NotFoundResult();
        }

        var size = SizeOf(context);
        var http = context.HttpContext;
        foreach (var source in stub.Sources.Where(source => stub.ServerId is not { } server || source.ServerId == server))
        {
            using var places = new HeldPlaces();
            if (await ReadAsync(source, size, anonymous, places, http.RequestAborted).ConfigureAwait(false) is { } image)
            {
                await WriteAsync(http, image).ConfigureAwait(false);
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
        FillHeight = NumberOf(context, "fillHeight")
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
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            http.Abort();
        }
    }

    // Anyone can ask for an image, so the reads have places of their own and never take one of playback. The places of the
    // image are held until its client has read it.
    private async Task<ReadOnlyMemory<byte>?> ReadAsync(StubSource source, RemotePhotoSize size, bool anonymous, HeldPlaces places, CancellationToken cancellationToken)
    {
        using var call = new HeldPlaces();
        if (!await TryEnterAsync(source.ServerId, anonymous, places, call, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var read = await CallAsync(source, size, anonymous, cancellationToken).ConfigureAwait(false);
        if (read.IsSuccess && PhotoThumbnails.IsCleanJpeg(read.Value!.Content.Span, JellyfinRemoteClient.PhotoImageSide))
        {
            return read.Value.Content;
        }

        if (read.Failure is RemoteFailure.Unreachable or RemoteFailure.Timeout or RemoteFailure.AuthenticationFailed
            or RemoteFailure.OtherServer or RemoteFailure.AdministratorAccount)
        {
            // A failed anonymous read pauses no signed-in user.
            (anonymous ? _anonymousPaused : _paused)[source.ServerId] = _clock.GetUtcNow() + PlaybackTimeouts.PhotoServerPause;
        }

        return null;
    }

    // Takes the places of a read. An anonymous one waits for its own first, in all and of the server, where it keeps none from
    // signed-in users.
    private async Task<bool> TryEnterAsync(Guid serverId, bool anonymous, HeldPlaces image, HeldPlaces call, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(_queueWait);
        try
        {
            if (anonymous)
            {
                await image.EnterAsync(_anonymous, wait.Token).ConfigureAwait(false);
                await call.EnterAsync(GateOf(_anonymousGates, serverId, PlaybackLimits.AnonymousPhotoImagesPerServer), wait.Token).ConfigureAwait(false);
            }

            await call.EnterAsync(GateOf(_gates, serverId, PlaybackLimits.PhotoImagesPerServer), wait.Token).ConfigureAwait(false);
            if (IsPaused(_paused, serverId) || (anonymous && IsPaused(_anonymousPaused, serverId)))
            {
                return false;
            }

            await image.EnterAsync(_inMemory, wait.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static SemaphoreSlim GateOf(ConcurrentDictionary<Guid, SemaphoreSlim> gates, Guid serverId, int places)
        => gates.GetOrAdd(serverId, _ => new SemaphoreSlim(places, places));

    private bool IsPaused(ConcurrentDictionary<Guid, DateTimeOffset> paused, Guid serverId)
        => paused.TryGetValue(serverId, out var until) && _clock.GetUtcNow() < until;

    private async Task<RemoteResult<RemoteImage>> CallAsync(StubSource source, RemotePhotoSize size, bool anonymous, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_store.Read(PlaybackTimes.From).StreamOpen);
        try
        {
            return anonymous
                ? await _registry.RunAnonymousCallAsync<RemoteImage>(source.ServerId, GetAsync, limit.Token).ConfigureAwait(false)
                : await _registry.RunPlaybackCallAsync<RemoteImage>(source.ServerId, GetAsync, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteResult.Fail<RemoteImage>(RemoteFailure.Timeout);
        }

        Task<RemoteResult<RemoteImage>> GetAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken token)
            => _client.GetPhotoImageAsync(endpoint, session, source.ItemId, size, token);
    }

    // The places a read holds, given back when it is disposed.
    private sealed class HeldPlaces : IDisposable
    {
        private readonly Stack<SemaphoreSlim> _held = new();

        // Waits for a place and holds it.
        public async Task EnterAsync(SemaphoreSlim places, CancellationToken cancellationToken)
        {
            await places.WaitAsync(cancellationToken).ConfigureAwait(false);
            _held.Push(places);
        }

        public void Dispose()
        {
            while (_held.TryPop(out var places))
            {
                places.Release();
            }
        }
    }
}
