using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
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
/// Logs nothing and lets no transport exception out: their messages can hold the remote address.
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
    /// Largest response accepted, in bytes.
    /// </summary>
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Timeout of a whole request.
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
    private const string ItemFields = "ProviderIds,MediaSources,DateCreated,Etag,Overview,Genres,People,Studios,Tags,Taglines,OriginalTitle,Chapters";

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] _importableCollectionTypes = ["movies", "tvshows"];
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

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteSession>> AuthenticateAsync(RemoteEndpoint endpoint, RemoteCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var body = JsonSerializer.SerializeToUtf8Bytes(new AuthenticateRequest(credentials.Username, credentials.Password));
        var response = await SendAsync(endpoint, HttpMethod.Post, "/Users/AuthenticateByName", null, body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return RemoteResult.Fail<RemoteSession>(response.Failure);
        }

        var result = Parse<AuthenticateResponse>(response.Body);
        if (string.IsNullOrEmpty(result?.AccessToken) || string.IsNullOrEmpty(result.User?.Id) || result.User.Policy is null)
        {
            return RemoteResult.Fail<RemoteSession>(RemoteFailure.InvalidResponse);
        }

        return result.User.Policy.IsAdministrator
            ? RemoteResult.Fail<RemoteSession>(RemoteFailure.AdministratorAccount)
            : RemoteResult.Success<RemoteSession>(new RemoteSession(result.AccessToken, result.User.Id));
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteCapabilities>> DetectCapabilitiesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var response = await SendAsync(endpoint, HttpMethod.Get, CapabilitiesPath, session, null, cancellationToken).ConfigureAwait(false);
        if (response.Status == HttpStatusCode.NotFound)
        {
            return RemoteResult.Success<RemoteCapabilities>(new RemoteCapabilities(RemoteMode.Standard, 0));
        }

        if (!response.IsSuccess)
        {
            return RemoteResult.Fail<RemoteCapabilities>(response.Failure);
        }

        var capabilities = Parse<CapabilitiesResponse>(response.Body);
        return capabilities is { ProtocolVersion: > 0 }
            ? RemoteResult.Success<RemoteCapabilities>(new RemoteCapabilities(RemoteMode.JellyMesh, capabilities.ProtocolVersion))
            : RemoteResult.Fail<RemoteCapabilities>(RemoteFailure.InvalidResponse);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        // /Library/VirtualFolders answers 403 to a non-admin user.
        return await ReadLibrariesAsync(endpoint, session, "/UserViews", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetSharedLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        return await ReadLibrariesAsync(endpoint, session, ProviderPath + "/Libraries", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteChangePage>> GetChangesAsync(RemoteEndpoint endpoint, RemoteSession session, string? cursor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = ProviderPath + "/Changes" + (cursor is null ? string.Empty : "?cursor=" + Uri.EscapeDataString(cursor));
        var response = await SendAsync(endpoint, HttpMethod.Get, path, session, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return RemoteResult.Fail<RemoteChangePage>(response.Failure);
        }

        var page = Parse<ChangesResponse>(response.Body);
        return page is { Cursor: { Length: > 0 } next, Changes: not null }
            ? RemoteResult.Success(new RemoteChangePage(page.Reset, next, page.HasMore, page.Changes))
            : RemoteResult.Fail<RemoteChangePage>(RemoteFailure.InvalidResponse);
    }

    private async Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> ReadLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, string path, CancellationToken cancellationToken)
    {
        var response = await SendAsync(endpoint, HttpMethod.Get, path, session, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return RemoteResult.Fail<IReadOnlyList<RemoteLibrary>>(response.Failure);
        }

        var views = Parse<UserViewsResponse>(response.Body);
        if (views?.Items is null)
        {
            return RemoteResult.Fail<IReadOnlyList<RemoteLibrary>>(RemoteFailure.InvalidResponse);
        }

        IReadOnlyList<RemoteLibrary> libraries = views.Items
            .Where(view => !string.IsNullOrEmpty(view.Id) && _importableCollectionTypes.Contains(view.CollectionType, StringComparer.OrdinalIgnoreCase))
            .Select(view => new RemoteLibrary(view.Id!, view.Name ?? string.Empty, view.CollectionType!))
            .ToList();
        return RemoteResult.Success<IReadOnlyList<RemoteLibrary>>(libraries);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteItemPage>> GetItemsAsync(RemoteEndpoint endpoint, RemoteSession session, RemoteItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(query);

        var response = await SendAsync(endpoint, HttpMethod.Get, ItemsPath(session, query), session, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return RemoteResult.Fail<RemoteItemPage>(response.Failure);
        }

        var page = Parse<ItemsResponse>(response.Body);
        return page is { Items: not null, TotalRecordCount: not null }
            ? RemoteResult.Success<RemoteItemPage>(new RemoteItemPage(WithQuality(page.Items, page.Quality), page.TotalRecordCount.Value))
            : RemoteResult.Fail<RemoteItemPage>(RemoteFailure.InvalidResponse);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<DateTimeOffset?>> GetServerTimeAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var response = await SendAsync(endpoint, HttpMethod.Get, "/System/Info/Public", session, null, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess
            ? RemoteResult.Success(response.Date)
            : RemoteResult.Fail<DateTimeOffset?>(response.Failure);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteImage>> GetImageAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, string imageType, int imageIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var path = string.Create(CultureInfo.InvariantCulture, $"/Items/{Uri.EscapeDataString(itemId)}/Images/{Uri.EscapeDataString(imageType)}/{imageIndex}");
        var response = await SendAsync(endpoint, HttpMethod.Get, path, session, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return RemoteResult.Fail<RemoteImage>(response.Failure);
        }

        return response.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true && response.Body.Length > 0
            ? RemoteResult.Success(new RemoteImage(response.Body, response.ContentType))
            : RemoteResult.Fail<RemoteImage>(RemoteFailure.InvalidResponse);
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
            "enableUserData=false",
            "fields=" + ItemFields,
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

    private static T? Parse<T>(byte[] body)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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

    private async Task<RawResponse> SendAsync(RemoteEndpoint endpoint, HttpMethod method, string path, RemoteSession? session, byte[]? body, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(HttpClientName);
        var uri = new Uri(endpoint.BaseUrl + path.TrimStart('/'));

        try
        {
            for (var redirects = 0; redirects <= MaxRedirects; redirects++)
            {
                using var request = BuildRequest(endpoint, method, uri, session, body);
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (FollowableTarget(response, method, uri) is not { } target)
                    {
                        return RawResponse.Failed(RemoteFailure.RedirectRefused, response.StatusCode);
                    }

                    uri = target;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return RawResponse.Failed(FailureOf(response.StatusCode, session), response.StatusCode);
                }

                var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return new RawResponse(response.StatusCode, content, RemoteFailure.None, response.Headers.Date, response.Content.Headers.ContentType?.MediaType);
            }

            return RawResponse.Failed(RemoteFailure.RedirectRefused, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RawResponse.Failed(RemoteFailure.Timeout, null);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            return RawResponse.Failed(RemoteFailure.ResponseTooLarge, null);
        }
        catch (HttpRequestException)
        {
            return RawResponse.Failed(RemoteFailure.Unreachable, null);
        }
    }

    private sealed record RawResponse(HttpStatusCode? Status, byte[] Body, RemoteFailure Failure, DateTimeOffset? Date = null, string? ContentType = null)
    {
        public bool IsSuccess => Failure == RemoteFailure.None;

        public static RawResponse Failed(RemoteFailure failure, HttpStatusCode? status) => new(status, [], failure);
    }

    private sealed record AuthenticateRequest(string Username, string Pw);

    private sealed record AuthenticateResponse(string? AccessToken, AuthenticatedUser? User);

    private sealed record AuthenticatedUser(string? Id, UserPolicy? Policy);

    private sealed record UserPolicy(bool IsAdministrator);

    private sealed record CapabilitiesResponse(int ProtocolVersion);

    private sealed record UserViewsResponse(IReadOnlyList<UserView>? Items);

    private sealed record UserView(string? Id, string? Name, string? CollectionType);

    private sealed record ItemsResponse(IReadOnlyList<RemoteItem>? Items, int? TotalRecordCount, IReadOnlyDictionary<string, RemoteQuality>? Quality);

    private sealed record ChangesResponse(bool Reset, string? Cursor, bool HasMore, IReadOnlyList<RemoteChange>? Changes);
}
