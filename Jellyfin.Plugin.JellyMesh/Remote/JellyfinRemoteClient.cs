using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// <see cref="IJellyfinRemoteClient"/> over HTTP.
/// </summary>
/// <remarks>
/// Logs nothing and lets no transport exception out: the whole failure goes in <see cref="RemoteResult{T}.Error"/>.
/// </remarks>
public sealed class JellyfinRemoteClient : IJellyfinRemoteClient
{
    /// <summary>
    /// Name of the <see cref="HttpClient"/> configuration.
    /// </summary>
    public const string HttpClientName = "JellyMesh.Remote";

    /// <summary>
    /// Path of the JellyMesh capability endpoint on a remote. The ProviderApi of Phase 7 serves it.
    /// </summary>
    public const string CapabilitiesPath = "/JellyMesh/Capabilities";

    /// <summary>
    /// Largest JSON answer accepted, in bytes. A page of the catalog, the largest there is, stays far below it.
    /// </summary>
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Largest image accepted, in bytes. Well above a poster or a 4K backdrop, and images are the one thing a remote can
    /// make this server keep on disk.
    /// </summary>
    public const int MaxImageBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Longest side of the local thumbnail of a remote photo, in pixels.
    /// </summary>
    public const int PhotoThumbnailSide = 640;

    /// <summary>
    /// Longest side of the image of a remote photo served to a client, in pixels. Above a 4K screen.
    /// </summary>
    public const int PhotoImageSide = 4096;

    /// <summary>
    /// Largest image of a remote photo served to a client, in bytes. It stays in memory until the client has read it.
    /// </summary>
    public const int MaxPhotoImageBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Largest local thumbnail of a remote photo, in bytes. Well above a JPEG of <see cref="PhotoThumbnailSide"/>.
    /// </summary>
    public const int MaxPhotoThumbnailBytes = 1024 * 1024;

    /// <summary>
    /// Highest JPEG quality of the image of a remote photo. Below 90, so that Jellyfin always encodes it again.
    /// </summary>
    public const int PhotoImageQuality = 80;

    /// <summary>
    /// Timeout of a whole request, the body included.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout to establish a connection.
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    // Without the profile Jellyfin may answer in camelCase; the parsing tolerates both anyway.
    private const string AcceptJson = "application/json; profile=\"PascalCase\"";
    private const int MaxRedirects = 3;
    private const string ProviderPath = "/" + ProviderProtocol.Route;

    // Everything a catalog entry keeps. ImageTags, BackdropImageTags, ratings, dates and numbers come by default, and
    // MediaSources holds the streams of each version. The list is what can reach the index: see RemoteItem.
    private const string ItemFields = "ProviderIds,MediaSources,DateCreated,Etag,Overview,Genres,People,Studios,Tags,Taglines,OriginalTitle,Chapters,RemoteTrailers,ParentId,Width,Height";

    // Waits before each new try of a sync read, and the longest wait a Retry-After can ask for (decision 47).
    // Each in pixels, the longest being the limit of the side of a photo image.
    private static readonly int[] _photoImageSides = [160, 320, PhotoThumbnailSide, 960, 1280, 1920, 2560, PhotoImageSide];
    private static readonly TimeSpan[] _retryWaits = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];
    private static readonly TimeSpan _maxRetryAfter = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinRemoteClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public JellyfinRemoteClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the timeout of a whole request.
    /// </summary>
    internal TimeSpan RequestLimit { get; init; } = RequestTimeout;

    /// <summary>
    /// Gets the wait between tries of a sync read.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteSession>> AuthenticateAsync(RemoteEndpoint endpoint, RemoteCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var body = JsonSerializer.SerializeToUtf8Bytes(new AuthenticateRequest(credentials.Username, credentials.Password));
        var response = await SendAsync(endpoint, HttpMethod.Post, "/Users/AuthenticateByName", null, body, cancellationToken, password: credentials.Password).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.FailAs<RemoteSession>();
        }

        var result = Parse<AuthenticateResponse>(response, out var error);
        if (!RemoteAnswers.CanGoInAHeader(result?.AccessToken))
        {
            return response.Refuse<RemoteSession>(error, result?.AccessToken);
        }

        // A refused login is handed back with its failure: the remote opened it, so it must be ended (419).
        var opened = new RemoteSession(result!.AccessToken!, result.User?.Id ?? string.Empty) { ServerId = ServerIdOf(result.ServerId) };
        if (string.IsNullOrEmpty(result.User?.Id) || result.User.Policy is null)
        {
            return new RemoteResult<RemoteSession>(opened, RemoteFailure.InvalidResponse, null, response.Call.Unusable(response.Status!.Value, response.Body, response.ContentType, null, opened.AccessToken));
        }

        return result.User.Policy.IsAdministrator ? RemoteResult.Fail(RemoteFailure.AdministratorAccount, opened) : RemoteResult.Success(opened);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<bool>> LogoutAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var response = await SendAsync(endpoint, HttpMethod.Post, "/Sessions/Logout", session, null, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? RemoteResult.Success(true) : response.FailAs<bool>();
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteCapabilities>> DetectCapabilitiesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var response = await ReadAsync(endpoint, CapabilitiesPath, session, cancellationToken).ConfigureAwait(false);
        if (response.Status == HttpStatusCode.NotFound)
        {
            return RemoteResult.Success<RemoteCapabilities>(new RemoteCapabilities(RemoteMode.Standard, 0));
        }

        if (!response.IsSuccess)
        {
            return response.FailAs<RemoteCapabilities>();
        }

        var capabilities = Parse<CapabilitiesResponse>(response, out var error);
        return capabilities is { ProtocolVersion: > 0 }
            ? RemoteResult.Success<RemoteCapabilities>(new RemoteCapabilities(RemoteMode.JellyMesh, capabilities.ProtocolVersion))
            : response.Refuse<RemoteCapabilities>(error);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // /Library/VirtualFolders answers 403 to a non-admin user. A library the user hid from My Media is still one it sees.
        return await ReadLibrariesAsync(endpoint, session, "/UserViews?includeHidden=true", true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetSharedLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        return await ReadLibrariesAsync(endpoint, session, ProviderPath + "/Libraries", false, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteChangePage>> GetChangesAsync(RemoteEndpoint endpoint, RemoteSession session, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = ProviderPath + "/Changes" + (cursor is null ? string.Empty : "?cursor=" + Uri.EscapeDataString(cursor));
        var response = await ReadAsync(endpoint, path, session, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.FailAs<RemoteChangePage>();
        }

        var page = Parse<ChangesResponse>(response, out var error);
        return page is { Cursor: { Length: > 0 } next, Changes: not null } && page.Changes.All(RemoteAnswers.IsWellFormed)
            ? RemoteResult.Success(new RemoteChangePage(page.Reset, next, page.HasMore, page.Changes) { ByteCount = response.Body.Length })
            : response.Refuse<RemoteChangePage>(error);
    }

    // The views of a user can group libraries in one that is no library of its own; the shared ones of a provider never do.
    private async Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> ReadLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, string path, bool userViews, CancellationToken cancellationToken)
    {
        var response = await ReadAsync(endpoint, path, session, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.FailAs<IReadOnlyList<RemoteLibrary>>();
        }

        var views = Parse<UserViewsResponse>(response, out var error);
        if (views?.Items is null)
        {
            return response.Refuse<IReadOnlyList<RemoteLibrary>>(error);
        }

        var libraries = views.Items
            .Where(view => !string.IsNullOrEmpty(view.Id))
            .Select(view => (View: view, Type: RemoteCollectionTypes.Of(view.CollectionType, view.Type)))
            .Where(entry => entry.Type is not null)
            .Select(entry => new RemoteLibrary(entry.View.Id!, entry.View.Name ?? string.Empty, entry.Type!)
            {
                MayCombine = userViews && !string.Equals(entry.View.Type, "CollectionFolder", StringComparison.Ordinal)
            })
            .ToList();
        return RemoteResult.Success<IReadOnlyList<RemoteLibrary>>(new RemoteLibraryList(libraries, views.Items.Count(view => !string.IsNullOrEmpty(view.Id))));
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteItemPage>> GetItemsAsync(RemoteEndpoint endpoint, RemoteSession session, RemoteItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(query);

        var response = await ReadAsync(endpoint, ItemsPath(session, query), session, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.FailAs<RemoteItemPage>();
        }

        var page = Parse<ItemsResponse>(response, out var error);
        return page is { Items: not null, TotalRecordCount: not null } && page.Items.All(RemoteAnswers.IsWellFormed)
            ? RemoteResult.Success(new RemoteItemPage(WithQuality(page.Items, page.Quality), page.TotalRecordCount.Value) { ByteCount = response.Body.Length })
            : response.Refuse<RemoteItemPage>(error);
    }

    /// <inheritdoc />
    public Task<RemoteResult<RemoteItemPage>> GetCollectionsAsync(RemoteEndpoint endpoint, RemoteSession session, string libraryId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        // With BoxSet as the only type, stock turns the library into "has a film in it" (checked on 12.4).
        return ReadBriefPageAsync(endpoint, session, libraryId, "includeItemTypes=BoxSet&fields=ProviderIds", startIndex, limit, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RemoteResult<RemoteItemPage>> GetCollectionFilmsAsync(RemoteEndpoint endpoint, RemoteSession session, string collectionId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        return ReadBriefPageAsync(endpoint, session, collectionId, "includeItemTypes=Movie&collapseBoxSetItems=false&excludeLocationTypes=Virtual", startIndex, limit, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteCollectionPage>> GetSharedCollectionsAsync(RemoteEndpoint endpoint, RemoteSession session, string libraryId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"{ProviderPath}/Collections?parentId={Uri.EscapeDataString(libraryId)}&startIndex={startIndex}&limit={limit}");
        var response = await ReadAsync(endpoint, path, session, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.FailAs<RemoteCollectionPage>();
        }

        var page = Parse<CollectionsResponse>(response, out var error);
        return page is { Items: not null, TotalRecordCount: not null } && page.Items.All(entry => entry is not null)
            ? RemoteResult.Success(new RemoteCollectionPage(page.Items, page.TotalRecordCount.Value) { ByteCount = response.Body.Length })
            : response.Refuse<RemoteCollectionPage>(error);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<DateTimeOffset?>> GetServerTimeAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var response = await ReadAsync(endpoint, "/System/Info/Public", session, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess
            ? RemoteResult.Success(response.Date)
            : response.FailAs<DateTimeOffset?>();
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemotePublicInfo>> GetPublicInfoAsync(RemoteEndpoint endpoint, RemoteSession? session, CancellationToken cancellationToken)
    {
        var response = await ReadAsync(endpoint, "/System/Info/Public", session, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.FailAs<RemotePublicInfo>();
        }

        var info = Parse<PublicInfoResponse>(response, out var error);
        return info is null
            ? response.Refuse<RemotePublicInfo>(error)
            : RemoteResult.Success(new RemotePublicInfo(LogText.Clean(info.ServerName) ?? string.Empty, ServerIdOf(info.Id)));
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteImage>> GetImageAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, string imageType, int imageIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = string.Create(CultureInfo.InvariantCulture, $"/Items/{Uri.EscapeDataString(itemId)}/Images/{Uri.EscapeDataString(imageType)}/{imageIndex}");
        var response = await SendAsync(endpoint, HttpMethod.Get, path, session, null, cancellationToken, MaxImageBytes).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.FailAs<RemoteImage>();
        }

        return response.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true && response.Body.Length > 0
            ? RemoteResult.Success(new RemoteImage(response.Body, response.ContentType))
            : response.Refuse<RemoteImage>(null);
    }

    /// <inheritdoc />
    public Task<RemoteResult<RemoteImage>> GetPhotoThumbnailAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, CancellationToken cancellationToken)
        => ReadPhotoAsync(endpoint, session, itemId, new RemotePhotoSize { MaxWidth = PhotoThumbnailSide, MaxHeight = PhotoThumbnailSide }, MaxPhotoThumbnailBytes, cancellationToken);

    /// <inheritdoc />
    public Task<RemoteResult<RemoteImage>> GetPhotoImageAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, RemotePhotoSize size, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(size);

        return ReadPhotoAsync(endpoint, session, itemId, size, MaxPhotoImageBytes, cancellationToken);
    }

    // Reads a re-encoded JPEG of a photo of at most a number of bytes.
    private async Task<RemoteResult<RemoteImage>> ReadPhotoAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, RemotePhotoSize size, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // A box to fill is asked with only the limit of each side (row 560); an exact size, as the largest one, so that
        // rounding a side never distorts the photo.
        var (fillWidth, fillHeight) = (StepOf(size.FillWidth), StepOf(size.FillHeight));
        var fills = fillWidth is not null || fillHeight is not null;
        var maxWidth = fills ? PhotoImageSide : SmallerStep(size.MaxWidth, size.Width);
        var maxHeight = fills ? PhotoImageSide : SmallerStep(size.MaxHeight, size.Height);

        // Below quality 90 Jellyfin never sends the original file, so the photo's own EXIF is not copied.
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/Items/{Uri.EscapeDataString(itemId)}/Images/Primary/0?maxWidth={maxWidth}&maxHeight={maxHeight}&quality={PhotoImageQuality}&format=Jpg");
        path += fillWidth is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"&fillWidth={fillWidth}");
        path += fillHeight is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"&fillHeight={fillHeight}");

        var response = await SendAsync(endpoint, HttpMethod.Get, path, session, null, cancellationToken, maxBytes).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return RemoteResult.Fail<RemoteImage>(response.Failure);
        }

        return string.Equals(response.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase) && response.Body.Length > 0
            ? RemoteResult.Success(new RemoteImage(response.Body, response.ContentType!))
            : RemoteResult.Fail<RemoteImage>(RemoteFailure.InvalidResponse);
    }

    // The first step not below an asked side, the last one for a longer side, null for none (row 534).
    private static int? StepOf(int? side)
        => side is > 0 ? _photoImageSides.FirstOrDefault(step => step >= side, PhotoImageSide) : null;

    // The smaller step of two asked sides, the last one for none.
    private static int SmallerStep(int? side, int? other)
        => Math.Min(StepOf(side) ?? PhotoImageSide, StepOf(other) ?? PhotoImageSide);

    // A page of names and IDs, in a fixed order, without the heavy fields of the catalog.
    private async Task<RemoteResult<RemoteItemPage>> ReadBriefPageAsync(RemoteEndpoint endpoint, RemoteSession session, string parentId, string filter, int startIndex, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"/Items?userId={Uri.EscapeDataString(session.UserId)}&parentId={Uri.EscapeDataString(parentId)}&recursive=true&{filter}&sortBy=SortName&sortOrder=Ascending&enableUserData=false&enableImages=false&startIndex={startIndex}&limit={limit}");
        var response = await ReadAsync(endpoint, path, session, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.FailAs<RemoteItemPage>();
        }

        var page = Parse<ItemsResponse>(response, out var error);
        return page is { Items: not null, TotalRecordCount: not null } && page.Items.All(RemoteAnswers.IsWellFormed)
            ? RemoteResult.Success(new RemoteItemPage(page.Items, page.TotalRecordCount.Value) { ByteCount = response.Body.Length })
            : response.Refuse<RemoteItemPage>(error);
    }

    private static IReadOnlyList<RemoteItem> WithQuality(IReadOnlyList<RemoteItem> items, IReadOnlyDictionary<string, RemoteQuality>? quality)
    {
        if (quality is null)
        {
            return items;
        }

        return items
            .Select(item => item with
            {
                MediaSources = item.MediaSources?.Select(source => source with { Quality = quality.GetValueOrDefault(source.Id) }).ToList()
            })
            .ToList();
    }

    private static string ItemsPath(RemoteSession session, RemoteItemQuery query)
    {
        // Sorted so that pages hold still: new items land at the end instead of shifting the pages already read.
        var parameters = new List<string>
        {
            "userId=" + Uri.EscapeDataString(session.UserId),
            "recursive=true",
            "includeItemTypes=" + query.Type,
            "sortBy=DateCreated,SortName",
            "sortOrder=Ascending",
            "excludeLocationTypes=Virtual",
            // A remote that groups films into collections would otherwise answer with the collection in their place.
            "collapseBoxSetItems=false",
            "enableUserData=false",
            // The path, which can name the remote's folders, only where the extension of the file is needed.
            "fields=" + ItemFields + (query.Type is RemoteItemType.Book or RemoteItemType.Photo ? ",Path" : string.Empty),
            string.Create(CultureInfo.InvariantCulture, $"startIndex={query.StartIndex}"),
            string.Create(CultureInfo.InvariantCulture, $"limit={query.Limit}")
        };

        if (query.LibraryId is not null)
        {
            parameters.Add("parentId=" + Uri.EscapeDataString(query.LibraryId));
        }

        if (query.Ids is { Count: > 0 })
        {
            parameters.Add("ids=" + string.Join(',', query.Ids.Select(Uri.EscapeDataString)));
        }

        // DateLastSaved moves on a new item, an edit, a replaced file or a new version, and not on user data. DateCreated
        // would miss edits, and Etag is a hash of DateLastSaved that no query can filter on.
        if (query.ChangedSinceUtc is { } since)
        {
            parameters.Add("minDateLastSaved=" + since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        }

        return (query.ViaProvider ? ProviderPath + "/Items?" : "/Items?") + string.Join('&', parameters);
    }

    private static T? Parse<T>(RawResponse response, out JsonException? error)
        where T : class
    {
        error = null;
        try
        {
            return JsonSerializer.Deserialize<T>(response.Body, _jsonOptions);
        }
        catch (JsonException exception)
        {
            error = exception;
            return null;
        }
    }

    // The Server Id a remote gives itself, as 32 lowercase digits. Anything that is not an id counts as none.
    private static string? ServerIdOf(string? value)
    {
        return Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("N") : null;
    }

    internal static string AuthorizationHeader(RemoteEndpoint endpoint, RemoteSession? session)
    {
        var header = string.Create(CultureInfo.InvariantCulture, $"MediaBrowser Client=\"JellyMesh\", Device=\"JellyMesh\", DeviceId=\"{endpoint.DeviceId}\", Version=\"{_version}\"");
        return session is null ? header : string.Create(CultureInfo.InvariantCulture, $"{header}, Token=\"{session.AccessToken}\"");
    }

    private static bool IsRedirect(HttpStatusCode status)
    {
        return status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    }

    private static bool IsSameOrigin(Uri first, Uri second)
    {
        return first.Scheme == second.Scheme
            && first.Port == second.Port
            && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? FollowableTarget(HttpResponseMessage response, HttpMethod method, Uri current)
    {
        // Only a 307 or 308 keeps the method and the body of a POST.
        var keepsRequest = method == HttpMethod.Get
            || response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
        if (!keepsRequest || response.Headers.Location is not { } location)
        {
            return null;
        }

        var target = new Uri(current, location);
        return IsSameOrigin(current, target) ? target : null;
    }

    private static RemoteFailure FailureOf(HttpStatusCode status, RemoteSession? session)
    {
        // Observed on 12.4: a refused or disabled token is 401 and a login of a disabled user is 403. So without a session
        // 403 is a refused login, and with one it says something about this call and not about the token.
        return status switch
        {
            HttpStatusCode.Unauthorized => RemoteFailure.AuthenticationFailed,
            HttpStatusCode.Forbidden => session is null ? RemoteFailure.AuthenticationFailed : RemoteFailure.Forbidden,
            _ => RemoteFailure.ServerError
        };
    }

    private static HttpRequestMessage BuildRequest(RemoteEndpoint endpoint, HttpMethod method, Uri uri, RemoteSession? session, byte[]? body)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Accept", AcceptJson);
        request.Headers.TryAddWithoutValidation("Authorization", AuthorizationHeader(endpoint, session));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        return request;
    }

    private static bool IsTransient(RawResponse response)
    {
        return response.Failure == RemoteFailure.Timeout
            || (response.Failure == RemoteFailure.ServerError && response.Status is HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);
    }

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - (response.Headers.Date ?? DateTimeOffset.UtcNow) : null;
    }

    // The planned wait, made longer by a Retry-After of the remote, up to the cap.
    private static TimeSpan WaitBefore(TimeSpan planned, TimeSpan? retryAfter)
    {
        return retryAfter is { } asked && asked > planned
            ? (asked < _maxRetryAfter ? asked : _maxRetryAfter)
            : planned;
    }

    // Sends a GET of the sync, and tries it again after a transient failure (decision 47).
    private async Task<RawResponse> ReadAsync(RemoteEndpoint endpoint, string path, RemoteSession? session, CancellationToken cancellationToken, [CallerMemberName] string step = "")
    {
        var response = await SendAsync(endpoint, HttpMethod.Get, path, session, null, cancellationToken, step: step).ConfigureAwait(false);
        var tries = 1;
        foreach (var wait in _retryWaits)
        {
            if (!IsTransient(response))
            {
                break;
            }

            await Delay(WaitBefore(wait, response.RetryAfter), cancellationToken).ConfigureAwait(false);
            response = await SendAsync(endpoint, HttpMethod.Get, path, session, null, cancellationToken, step: step).ConfigureAwait(false);
            tries++;
        }

        return response.Error is { } error ? response with { Error = error with { Tries = tries } } : response;
    }

    private async Task<RawResponse> SendAsync(
        RemoteEndpoint endpoint,
        HttpMethod method,
        string path,
        RemoteSession? session,
        byte[]? body,
        CancellationToken cancellationToken,
        int maxBytes = MaxResponseBytes,
        string? password = null,
        [CallerMemberName] string step = "")
    {
        if (!RemoteUrl.TryCombine(endpoint.BaseUrl, path, out var uri))
        {
            var refused = new RemoteCall(step, method, path, session?.AccessToken, password);
            return RawResponse.Failed(refused, RemoteFailure.InvalidResponse, null, refused.Error(null, "The path cannot be joined to the address of the server; nothing was sent"));
        }

        var call = new RemoteCall(step, method, uri.AbsoluteUri, session?.AccessToken, password);

        using var client = _httpClientFactory.CreateClient(HttpClientName);

        // Headers are read first, and the client's own timeout stops there: this one also covers a body that stops arriving.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestLimit);

        try
        {
            for (var redirects = 0; redirects <= MaxRedirects; redirects++)
            {
                using var request = BuildRequest(endpoint, method, uri, session, body);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (FollowableTarget(response, method, uri) is not { } target)
                    {
                        var refused = await call.ErrorAsync(response, "The redirect is not followed", deadline.Token).ConfigureAwait(false);
                        return RawResponse.Failed(call, RemoteFailure.RedirectRefused, response.StatusCode, refused);
                    }

                    uri = target;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var refused = await call.ErrorAsync(response, null, deadline.Token).ConfigureAwait(false);
                    return RawResponse.Failed(call, FailureOf(response.StatusCode, session), response.StatusCode, refused, RetryAfterOf(response));
                }

                var content = await RemoteBody.ReadLimitedAsync(response.Content, maxBytes, deadline.Token).ConfigureAwait(false);
                return content is null
                    ? RawResponse.Failed(call, RemoteFailure.ResponseTooLarge, response.StatusCode, call.TooLarge(response.StatusCode, maxBytes))
                    : new RawResponse(call, response.StatusCode, content, RemoteFailure.None)
                    {
                        Date = response.Headers.Date,
                        ContentType = response.Content.Headers.ContentType?.MediaType
                    };
            }

            return RawResponse.Failed(call, RemoteFailure.RedirectRefused, null, call.Error(null, string.Create(CultureInfo.InvariantCulture, $"More than {MaxRedirects} redirects")));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return RawResponse.Failed(call, RemoteFailure.Timeout, null, call.Error(exception, "The server did not answer in time"));
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            return RawResponse.Failed(call, RemoteFailure.ResponseTooLarge, null, call.Error(exception));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return RawResponse.Failed(call, RemoteFailure.Unreachable, null, call.Error(exception));
        }
    }

    private sealed record RawResponse(RemoteCall Call, HttpStatusCode? Status, byte[] Body, RemoteFailure Failure)
    {
        public bool IsSuccess => Failure == RemoteFailure.None;

        public DateTimeOffset? Date { get; init; }

        public string? ContentType { get; init; }

        public TimeSpan? RetryAfter { get; init; }

        public RemoteError? Error { get; init; }

        public static RawResponse Failed(RemoteCall call, RemoteFailure failure, HttpStatusCode? status, RemoteError error, TimeSpan? retryAfter = null)
            => new(call, status, [], failure) { RetryAfter = retryAfter, Error = error };

        public RemoteResult<T> FailAs<T>() => RemoteResult.FailWithError<T>(Failure, Failure == RemoteFailure.ResponseTooLarge ? null : (int?)Status, Error);

        // Refuses an answer that was read, with the exception of its parsing and a token it holds.
        public RemoteResult<T> Refuse<T>(JsonException? exception, string? token = null) => RemoteResult.FailWithError<T>(
            RemoteFailure.InvalidResponse,
            null,
            Call.Unusable(Status!.Value, Body, ContentType, exception, token));
    }

    private sealed record AuthenticateRequest(string Username, string Pw);

    private sealed record AuthenticateResponse(string? AccessToken, AuthenticatedUser? User, string? ServerId);

    private sealed record AuthenticatedUser(string? Id, UserPolicy? Policy);

    private sealed record UserPolicy(bool IsAdministrator);

    private sealed record CapabilitiesResponse(int ProtocolVersion);

    private sealed record PublicInfoResponse(string? ServerName, string? Id);

    private sealed record UserViewsResponse(IReadOnlyList<UserView>? Items);

    private sealed record UserView(string? Id, string? Name, string? CollectionType, string? Type);

    private sealed record ItemsResponse(IReadOnlyList<RemoteItem>? Items, int? TotalRecordCount, IReadOnlyDictionary<string, RemoteQuality>? Quality);

    private sealed record CollectionsResponse(IReadOnlyList<RemoteCollectionEntry>? Items, int? TotalRecordCount);

    private sealed record ChangesResponse(bool Reset, string? Cursor, bool HasMore, IReadOnlyList<RemoteChange>? Changes);
}
