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
public sealed class PhotoImageFilter : IAsyncActionFilter
{
    private const string ImageController = "Image";

    private const string ImageMediaType = "image/jpeg";

    private readonly StubLookup _stubs;
    private readonly IAuthorizationContext _authorization;
    private readonly IRemoteServerRegistry _registry;
    private readonly IJellyfinRemoteClient _client;
    private readonly IPluginConfigurationStore _store;
    private readonly TimeSpan _queueWait;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PhotoImageFilter"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    /// <param name="authorization">The authorization context.</param>
    /// <param name="registry">The remote servers.</param>
    /// <param name="client">The client of the remotes.</param>
    /// <param name="store">The configuration, for the time limit.</param>
    public PhotoImageFilter(StubLookup stubs, IAuthorizationContext authorization, IRemoteServerRegistry registry, IJellyfinRemoteClient client, IPluginConfigurationStore store)
        : this(stubs, authorization, registry, client, store, PlaybackLimits.PhotoImageQueueWait)
    {
    }

    // Takes how long a read waits for a place, so that tests can shorten it.
    internal PhotoImageFilter(StubLookup stubs, IAuthorizationContext authorization, IRemoteServerRegistry registry, IJellyfinRemoteClient client, IPluginConfigurationStore store, TimeSpan queueWait)
    {
        _stubs = stubs;
        _authorization = authorization;
        _registry = registry;
        _client = client;
        _store = store;
        _queueWait = queueWait;
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
            var read = await ReadAsync(source, size, http.RequestAborted).ConfigureAwait(false);
            if (!read.IsSuccess || !PhotoThumbnails.IsCleanJpeg(read.Value!.Content.Span, JellyfinRemoteClient.PhotoImageSide))
            {
                continue;
            }

            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentDisposition = "attachment";
            http.Response.Headers.CacheControl = "private, no-cache";
            return new FileContentResult(read.Value.Content.ToArray(), ImageMediaType);
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

    // Anyone can ask for an image, so the reads have places of their own and never take one of playback.
    private async Task<RemoteResult<RemoteImage>> ReadAsync(StubSource source, RemotePhotoSize size, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(source.ServerId, _ => new SemaphoreSlim(PlaybackLimits.PhotoImagesPerServer, PlaybackLimits.PhotoImagesPerServer));
        if (!await gate.WaitAsync(_queueWait, cancellationToken).ConfigureAwait(false))
        {
            return RemoteResult.Fail<RemoteImage>(RemoteFailure.Busy);
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_store.Read(PlaybackTimes.From).StreamOpen);
            return await _registry.RunPlaybackCallAsync(
                source.ServerId,
                (endpoint, session, token) => _client.GetPhotoImageAsync(endpoint, session, source.ItemId, size, token),
                limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RemoteResult.Fail<RemoteImage>(RemoteFailure.Timeout);
        }
        finally
        {
            gate.Release();
        }
    }
}
