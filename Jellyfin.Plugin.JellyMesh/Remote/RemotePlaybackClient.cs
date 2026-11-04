using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// <see cref="IRemotePlaybackClient"/> over HTTP.
/// </summary>
/// <remarks>
/// Logs nothing and lets no transport exception out, as <see cref="JellyfinRemoteClient"/>: their messages can hold the
/// remote address. Cancellation goes through, so that the caller can apply its own timeouts.
/// </remarks>
public sealed class RemotePlaybackClient : IRemotePlaybackClient
{
    /// <summary>
    /// Name of the <see cref="HttpClient"/> configuration. It has no overall timeout: a stream lasts as long as the film.
    /// </summary>
    public const string HttpClientName = "JellyMesh.Stream";

    /// <summary>
    /// Largest document accepted, in bytes.
    /// </summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    private const string AcceptJson = "application/json; profile=\"PascalCase\"";

    private static readonly string _userAgent = "JellyMesh/" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0");

    private static readonly JsonSerializerOptions _requestOptions = new(JsonDefaults.PascalCaseOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemotePlaybackClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public RemotePlaybackClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets the one User-Agent sent to remotes. Never the client's: the remote hashes it into the transcode path, so a
    /// second value within a session starts a second ffmpeg that the stop cannot reach.
    /// </summary>
    public static string UserAgent => _userAgent;

    /// <inheritdoc />
    public async Task<RemoteResult<PlaybackInfoResponse>> GetPlaybackInfoAsync(RemoteEndpoint endpoint, RemoteSession session, RemotePlaybackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var body = new PlaybackInfoBody(
            session.UserId,
            request.MediaSourceId,
            request.DeviceProfile,
            request.AudioStreamIndex,
            request.SubtitleStreamIndex,
            request.StartTimeTicks,
            request.MaxStreamingBitrate,
            request.MaxAudioChannels,
            request.EnableDirectPlay,
            request.EnableDirectStream,
            request.EnableTranscoding,
            request.AllowVideoStreamCopy,
            request.AllowAudioStreamCopy,
            request.AlwaysBurnInSubtitleWhenTranscoding);

        using var message = BuildRequest(endpoint, session, HttpMethod.Post, "/Items/" + Uri.EscapeDataString(request.ItemId) + "/PlaybackInfo");
        message.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, _requestOptions));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (request.ReplacesPlaySessionId is { } replaces)
        {
            message.Headers.TryAddWithoutValidation(ProviderProtocol.ReplacesHeader, replaces);
        }

        var document = await ReadDocumentAsync(message, cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return RemoteResult.Fail<PlaybackInfoResponse>(document.Failure);
        }

        try
        {
            var info = JsonSerializer.Deserialize<PlaybackInfoResponse>(document.Value!, JsonDefaults.PascalCaseOptions);
            return info is null
                ? RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.InvalidResponse)
                : RemoteResult.Success(info);
        }
        catch (JsonException)
        {
            return RemoteResult.Fail<PlaybackInfoResponse>(RemoteFailure.InvalidResponse);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteTranscodePolicy>> GetTranscodePolicyAsync(RemoteEndpoint endpoint, RemoteSession session, bool readHardware, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        using var userRequest = BuildRequest(endpoint, session, HttpMethod.Get, "/Users/Me");
        var user = await ReadDocumentAsync(userRequest, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return RemoteResult.Fail<RemoteTranscodePolicy>(user.Failure);
        }

        if (Parse<UserResponse>(user.Value!)?.Policy is not { } policy)
        {
            return RemoteResult.Fail<RemoteTranscodePolicy>(RemoteFailure.InvalidResponse);
        }

        return RemoteResult.Success(new RemoteTranscodePolicy(
            readHardware ? await ReadHardwareAsync(endpoint, session, cancellationToken).ConfigureAwait(false) : null,
            policy.EnableVideoPlaybackTranscoding,
            policy.EnableAudioPlaybackTranscoding,
            policy.EnablePlaybackRemuxing));
    }

    /// <inheritdoc />
    public async Task<RemoteResult<string>> ReadPlaylistAsync(RemoteEndpoint endpoint, RemoteSession session, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        using var message = BuildRequest(endpoint, session, HttpMethod.Get, path);
        var document = await ReadDocumentAsync(message, cancellationToken).ConfigureAwait(false);
        return document.IsSuccess
            ? RemoteResult.Success(Encoding.UTF8.GetString(document.Value!))
            : RemoteResult.Fail<string>(document.Failure);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteContent>> OpenStreamAsync(RemoteEndpoint endpoint, RemoteSession session, RemoteStreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        using var message = BuildRequest(endpoint, session, request.Method, request.Path);
        if (!string.IsNullOrEmpty(request.Range))
        {
            message.Headers.TryAddWithoutValidation("Range", request.Range);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return RemoteResult.Fail<RemoteContent>(RemoteFailure.Unreachable);
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var failure = FailureOf(response);
            response.Dispose();
            return RemoteResult.Fail<RemoteContent>(failure);
        }

        var content = response.Content.Headers;
        var headers = new RemoteContentHeaders(
            content.ContentType?.ToString(),
            content.ContentLength,
            content.ContentRange?.ToString(),
            response.Headers.AcceptRanges.Count > 0 ? string.Join(',', response.Headers.AcceptRanges) : null);

        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return RemoteResult.Success(new RemoteContent(response.StatusCode, headers, body, response));
        }
        catch (HttpRequestException)
        {
            response.Dispose();
            return RemoteResult.Fail<RemoteContent>(RemoteFailure.Unreachable);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteResult<bool>> StopEncodingAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        var path = "/Videos/ActiveEncodings?DeviceId=" + Uri.EscapeDataString(endpoint.DeviceId) + "&PlaySessionId=" + Uri.EscapeDataString(playSessionId);
        using var message = BuildRequest(endpoint, session, HttpMethod.Delete, path);
        var document = await ReadDocumentAsync(message, cancellationToken).ConfigureAwait(false);
        return document.IsSuccess ? RemoteResult.Success(true) : RemoteResult.Fail<bool>(document.Failure);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<bool>> RenewStreamAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        using var message = BuildRequest(endpoint, session, HttpMethod.Put, StreamPath(playSessionId));
        var document = await ReadDocumentAsync(message, cancellationToken).ConfigureAwait(false);
        return document.IsSuccess ? RemoteResult.Success(true) : RemoteResult.Fail<bool>(document.Failure);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<bool>> ReleaseStreamAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        using var message = BuildRequest(endpoint, session, HttpMethod.Delete, StreamPath(playSessionId));
        var document = await ReadDocumentAsync(message, cancellationToken).ConfigureAwait(false);
        return document.IsSuccess ? RemoteResult.Success(true) : RemoteResult.Fail<bool>(document.Failure);
    }

    private static string StreamPath(string playSessionId) => "/" + ProviderProtocol.Route + "/Streams/" + Uri.EscapeDataString(playSessionId);

    private static HttpRequestMessage BuildRequest(RemoteEndpoint endpoint, RemoteSession session, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUrl + path.TrimStart('/')));
        request.Headers.TryAddWithoutValidation("Accept", AcceptJson);
        request.Headers.TryAddWithoutValidation("Authorization", JellyfinRemoteClient.AuthorizationHeader(endpoint, session));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        return request;
    }

    private static RemoteFailure FailureOf(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        var code = (int)status;
        if (code is >= 300 and < 400)
        {
            return RemoteFailure.RedirectRefused;
        }

        // The header, not the status: a proxy can answer 429 for its own reasons, and that says nothing about a limit.
        if (status == HttpStatusCode.TooManyRequests
            && response.Headers.TryGetValues(ProviderProtocol.RefusalHeader, out var refusal)
            && refusal.Contains(ProviderProtocol.StreamLimitRefusal, StringComparer.Ordinal))
        {
            return RemoteFailure.StreamLimitReached;
        }

        // Only 401 speaks of the token. A login on the same device invalidates the token in use, so a closed route must
        // not cost one.
        return status switch
        {
            HttpStatusCode.Unauthorized => RemoteFailure.AuthenticationFailed,
            HttpStatusCode.Forbidden => RemoteFailure.Forbidden,
            _ => RemoteFailure.ServerError
        };
    }

    private static T? Parse<T>(byte[] body)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, _readOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxDocumentBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return buffer.ToArray();
        }
    }

    private async Task<string?> ReadHardwareAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        using var message = BuildRequest(endpoint, session, HttpMethod.Get, "/System/Configuration/encoding");
        var encoding = await ReadDocumentAsync(message, cancellationToken).ConfigureAwait(false);
        return encoding.IsSuccess ? Parse<EncodingResponse>(encoding.Value!)?.HardwareAccelerationType : null;
    }

    private async Task<RemoteResult<byte[]>> ReadDocumentAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return RemoteResult.Fail<byte[]>(FailureOf(response));
            }

            var body = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return body is null
                ? RemoteResult.Fail<byte[]>(RemoteFailure.ResponseTooLarge)
                : RemoteResult.Success(body);
        }
        catch (HttpRequestException)
        {
            return RemoteResult.Fail<byte[]>(RemoteFailure.Unreachable);
        }
        catch (IOException)
        {
            return RemoteResult.Fail<byte[]>(RemoteFailure.Unreachable);
        }
    }

    private sealed record PlaybackInfoBody(
        string UserId,
        string MediaSourceId,
        DeviceProfile? DeviceProfile,
        int? AudioStreamIndex,
        int? SubtitleStreamIndex,
        long? StartTimeTicks,
        int? MaxStreamingBitrate,
        int? MaxAudioChannels,
        bool? EnableDirectPlay,
        bool? EnableDirectStream,
        bool? EnableTranscoding,
        bool? AllowVideoStreamCopy,
        bool? AllowAudioStreamCopy,
        bool? AlwaysBurnInSubtitleWhenTranscoding);

    private sealed record UserResponse(UserPolicyResponse? Policy);

    private sealed record UserPolicyResponse(bool EnableVideoPlaybackTranscoding, bool EnableAudioPlaybackTranscoding, bool EnablePlaybackRemuxing);

    private sealed record EncodingResponse(string? HardwareAccelerationType);
}
