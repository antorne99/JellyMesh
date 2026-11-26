using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// <see cref="IRemotePlaybackClient"/> over HTTP.
/// </summary>
/// <remarks>
/// Logs nothing and lets no transport exception out, as <see cref="JellyfinRemoteClient"/>: the whole failure goes in
/// <see cref="RemoteResult{T}.Error"/>. Cancellation goes through, so that the caller can apply its own timeouts.
/// </remarks>
public sealed class RemotePlaybackClient : IRemotePlaybackClient, IRemoteSegmentClient
{
    /// <summary>
    /// Name of the <see cref="HttpClient"/> configuration. It has no overall timeout: a stream lasts as long as the film.
    /// </summary>
    public const string HttpClientName = "JellyMesh.Stream";

    /// <summary>
    /// Largest document accepted, in bytes. A variant playlist is the largest: each segment line of a stock server takes
    /// close to a kilobyte, so a film of many hours needs several megabytes.
    /// </summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Most segments kept for one item. A film has a handful; the rest would only be rows a remote makes us store.
    /// </summary>
    public const int MaxSegments = 64;

    /// <summary>
    /// Most lines kept of the lyrics of a track. A song has a few dozen.
    /// </summary>
    public const int MaxLyricLines = 2000;

    /// <summary>
    /// Most timed cues kept for one line of lyrics, one per word or syllable.
    /// </summary>
    public const int MaxLyricCues = 512;

    // Largest lyrics document accepted, in bytes.
    private const int MaxLyricsBytes = 1024 * 1024;

    private const string AcceptJson = "application/json; profile=\"PascalCase\"";

    private static readonly string _userAgent = "JellyMesh/" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0");

    private static readonly JsonSerializerOptions _requestOptions = new(JsonDefaults.PascalCaseOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpRequestOptionsKey<string> _refusedPath = new("JellyMesh.RefusedPath");

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
        if (message.RequestUri is null)
        {
            return RefusedPath<PlaybackInfoResponse>(message, session);
        }

        message.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, _requestOptions));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (request.ReplacesPlaySessionId is { } replaces)
        {
            message.Headers.TryAddWithoutValidation(ProviderProtocol.ReplacesHeader, replaces);
        }

        NameDevice(message, request.Device);

        var document = await ReadDocumentAsync(message, session, cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return document.FailAs<PlaybackInfoResponse>();
        }

        // Besides invalid JSON, the converters of the server throw what they like on a value of the wrong shape.
        try
        {
            var info = JsonSerializer.Deserialize<PlaybackInfoResponse>(document.Value!.Body, JsonDefaults.PascalCaseOptions);
            return RemoteAnswers.IsWellFormed(info)
                ? RemoteResult.Success(info!)
                : document.Value!.Refuse<PlaybackInfoResponse>(null);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
        {
            return document.Value!.Refuse<PlaybackInfoResponse>(exception);
        }
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteTranscodePolicy>> GetTranscodePolicyAsync(RemoteEndpoint endpoint, RemoteSession session, bool readHardware, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        using var userRequest = BuildRequest(endpoint, session, HttpMethod.Get, "/Users/Me");
        var user = await ReadDocumentAsync(userRequest, session, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.FailAs<RemoteTranscodePolicy>();
        }

        if (Parse<UserResponse>(user.Value!, out var error)?.Policy is not { } policy)
        {
            return user.Value!.Refuse<RemoteTranscodePolicy>(error);
        }

        return RemoteResult.Success(new RemoteTranscodePolicy(
            readHardware ? await ReadHardwareAsync(endpoint, session, cancellationToken).ConfigureAwait(false) : null,
            policy.EnableVideoPlaybackTranscoding,
            policy.EnableAudioPlaybackTranscoding,
            policy.EnablePlaybackRemuxing));
    }

    /// <inheritdoc />
    public async Task<RemoteResult<string>> ReadPlaylistAsync(RemoteEndpoint endpoint, RemoteSession session, string path, string? device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        using var message = BuildRequest(endpoint, session, HttpMethod.Get, path);
        NameDevice(message, device);
        var document = await ReadDocumentAsync(message, session, cancellationToken).ConfigureAwait(false);
        return document.IsSuccess
            ? RemoteResult.Success(Encoding.UTF8.GetString(document.Value!.Body))
            : document.FailAs<string>();
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteContent>> OpenStreamAsync(RemoteEndpoint endpoint, RemoteSession session, RemoteStreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        using var message = BuildRequest(endpoint, session, request.Method, request.Path);
        if (message.RequestUri is null)
        {
            return RefusedPath<RemoteContent>(message, session);
        }

        var call = CallOf(message, session);

        if (!string.IsNullOrEmpty(request.Range))
        {
            message.Headers.TryAddWithoutValidation("Range", request.Range);
        }

        if (request.IsProbe)
        {
            message.Headers.TryAddWithoutValidation(ProviderProtocol.ProbeHeader, "1");
        }

        NameDevice(message, request.Device);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return RemoteResult.FailWithError<RemoteContent>(RemoteFailure.Unreachable, null, call.Error(exception));
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var failure = FailureOf(response);
            var status = (int)response.StatusCode;
            var error = await call.ErrorAsync(response, null, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            return RemoteResult.FailWithError<RemoteContent>(failure, status, error);
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
        catch (HttpRequestException exception)
        {
            response.Dispose();
            return RemoteResult.FailWithError<RemoteContent>(RemoteFailure.Unreachable, null, call.Error(exception));
        }
        catch (OperationCanceledException)
        {
            response.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteLyrics>> GetLyricsAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(itemId);

        using var message = BuildRequest(endpoint, session, HttpMethod.Get, "/Audio/" + Uri.EscapeDataString(itemId) + "/Lyrics");
        var document = await ReadDocumentAsync(message, session, cancellationToken, MaxLyricsBytes).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return document.Status == (int)HttpStatusCode.NotFound ? RemoteResult.Success(RemoteLyrics.None) : document.FailAs<RemoteLyrics>();
        }

        return Parse<LyricsBody>(document.Value!, out var error) is { } body
            ? RemoteResult.Success(new RemoteLyrics(ToLyrics(body)))
            : document.Value!.Refuse<RemoteLyrics>(error);
    }

    /// <inheritdoc />
    public async Task<RemoteResult<bool>> StopEncodingAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        var path = "/Videos/ActiveEncodings?DeviceId=" + Uri.EscapeDataString(endpoint.DeviceId) + "&PlaySessionId=" + Uri.EscapeDataString(playSessionId);
        using var message = BuildRequest(endpoint, session, HttpMethod.Delete, path);
        var document = await ReadDocumentAsync(message, session, cancellationToken).ConfigureAwait(false);
        return document.IsSuccess ? RemoteResult.Success(true) : document.FailAs<bool>();
    }

    /// <inheritdoc />
    public async Task<RemoteResult<bool>> RenewStreamAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        using var message = BuildRequest(endpoint, session, HttpMethod.Put, StreamPath(playSessionId));
        var document = await ReadDocumentAsync(message, session, cancellationToken).ConfigureAwait(false);
        return document.IsSuccess ? RemoteResult.Success(true) : document.FailAs<bool>();
    }

    /// <inheritdoc />
    public async Task<RemoteResult<bool>> ReleaseStreamAsync(RemoteEndpoint endpoint, RemoteSession session, string playSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);

        using var message = BuildRequest(endpoint, session, HttpMethod.Delete, StreamPath(playSessionId));
        var document = await ReadDocumentAsync(message, session, cancellationToken).ConfigureAwait(false);
        return document.IsSuccess ? RemoteResult.Success(true) : document.FailAs<bool>();
    }

    /// <inheritdoc />
    public async Task<RemoteResult<RemoteSegmentRead>> GetSegmentsAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(itemId);

        using var message = BuildRequest(endpoint, session, HttpMethod.Get, "/MediaSegments/" + Uri.EscapeDataString(itemId));
        var document = await ReadDocumentAsync(message, session, cancellationToken).ConfigureAwait(false);
        if (!document.IsSuccess)
        {
            return IsItemRefusal(document.Status)
                ? RemoteResult.Success(RemoteSegmentRead.Refused)
                : document.FailAs<RemoteSegmentRead>();
        }

        if (Parse<SegmentPage>(document.Value!, out var error)?.Items is not { } items)
        {
            return document.Value!.Refuse<RemoteSegmentRead>(error);
        }

        var segments = items
            .Select(ToSegment)
            .OfType<RemoteSegment>()
            .Take(MaxSegments)
            .ToList();
        return RemoteResult.Success(new RemoteSegmentRead(segments));
    }

    // A client error about the item alone, such as not found or too many reads at once; 401 and 403 speak of the user.
    private static bool IsItemRefusal(int? status)
    {
        return status is >= 400 and < 500
            and not (int)HttpStatusCode.Unauthorized
            and not (int)HttpStatusCode.Forbidden;
    }

    // Null for a type Jellyfin does not name, or times stock would refuse to store.
    private static RemoteSegment? ToSegment(SegmentEntry? entry)
    {
        if (entry?.Type is not { Length: > 0 } name
            || !name.All(char.IsAsciiLetter)
            || !Enum.TryParse<MediaSegmentType>(name, out var type)
            || type == MediaSegmentType.Unknown
            || entry.StartTicks < 0
            || entry.EndTicks <= entry.StartTicks)
        {
            return null;
        }

        return new RemoteSegment(type, entry.StartTicks, entry.EndTicks);
    }

    // Copies the fields stock defines, and nothing else the remote sent.
    private static LyricDto ToLyrics(LyricsBody body)
    {
        var metadata = body.Metadata;
        return new LyricDto
        {
            Metadata = new LyricMetadata
            {
                Artist = metadata?.Artist,
                Album = metadata?.Album,
                Title = metadata?.Title,
                Author = metadata?.Author,
                Length = metadata?.Length,
                By = metadata?.By,
                Offset = metadata?.Offset,
                Creator = metadata?.Creator,
                Version = metadata?.Version,
                IsSynced = metadata?.IsSynced
            },
            Lyrics = (body.Lyrics ?? [])
                .Where(line => line?.Text is not null)
                .Take(MaxLyricLines)
                .Select(line => new LyricLine(
                    line!.Text!,
                    line.Start,
                    line.Cues?.OfType<LyricCueBody>().Take(MaxLyricCues).Select(cue => new LyricLineCue(cue.Position, cue.EndPosition, cue.Start, cue.End)).ToList()))
                .ToList()
        };
    }

    private static string StreamPath(string playSessionId) => "/" + ProviderProtocol.Route + "/Streams/" + Uri.EscapeDataString(playSessionId);

    // Without a URL when the path cannot be accepted: paths come from the answers of the remote.
    private static HttpRequestMessage BuildRequest(RemoteEndpoint endpoint, RemoteSession session, HttpMethod method, string path)
    {
        if (!RemoteUrl.TryCombine(endpoint.BaseUrl, path, out var uri))
        {
            var refused = new HttpRequestMessage(method, (Uri?)null);
            refused.Options.Set(_refusedPath, path);
            return refused;
        }

        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("Accept", AcceptJson);
        request.Headers.TryAddWithoutValidation("Authorization", JellyfinRemoteClient.AuthorizationHeader(endpoint, session));
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        return request;
    }

    private static RemoteCall CallOf(HttpRequestMessage message, RemoteSession session, [CallerMemberName] string step = "")
    {
        var url = message.RequestUri?.AbsoluteUri ?? (message.Options.TryGetValue(_refusedPath, out var path) ? path : string.Empty);
        return new RemoteCall(step, message.Method, url, session.AccessToken);
    }

    private static RemoteResult<T> RefusedPath<T>(HttpRequestMessage message, RemoteSession session, [CallerMemberName] string step = "")
    {
        var error = CallOf(message, session, step).Error(null, "The path cannot be joined to the address of the server; nothing was sent");
        return RemoteResult.FailWithError<T>(RemoteFailure.InvalidResponse, null, error);
    }

    // Names the device of a track (row 511). The callers pass one for a JellyMesh remote only.
    private static void NameDevice(HttpRequestMessage? message, string? device)
    {
        if (device is not null)
        {
            message?.Headers.TryAddWithoutValidation(ProviderProtocol.DeviceHeader, device);
        }
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

    private static T? Parse<T>(RemoteDocument document, out JsonException? error)
        where T : class
    {
        error = null;
        try
        {
            return JsonSerializer.Deserialize<T>(document.Body, _readOptions);
        }
        catch (JsonException exception)
        {
            error = exception;
            return null;
        }
    }

    private async Task<string?> ReadHardwareAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        using var message = BuildRequest(endpoint, session, HttpMethod.Get, "/System/Configuration/encoding");
        var encoding = await ReadDocumentAsync(message, session, cancellationToken).ConfigureAwait(false);
        return encoding.IsSuccess ? Parse<EncodingResponse>(encoding.Value!, out _)?.HardwareAccelerationType : null;
    }

    // Reads a document of at most maxBytes. A failure keeps the status of the answer, if any.
    private async Task<RemoteResult<RemoteDocument>> ReadDocumentAsync(HttpRequestMessage message, RemoteSession session, CancellationToken cancellationToken, int maxBytes = MaxDocumentBytes, [CallerMemberName] string step = "")
    {
        if (message.RequestUri is null)
        {
            return RefusedPath<RemoteDocument>(message, session, step);
        }

        var call = CallOf(message, session, step);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var refused = await call.ErrorAsync(response, null, cancellationToken).ConfigureAwait(false);
                return RemoteResult.FailWithError<RemoteDocument>(FailureOf(response), (int)response.StatusCode, refused);
            }

            var body = await RemoteBody.ReadLimitedAsync(response.Content, maxBytes, cancellationToken).ConfigureAwait(false);
            return body is null
                ? RemoteResult.FailWithError<RemoteDocument>(RemoteFailure.ResponseTooLarge, null, call.TooLarge(response.StatusCode, maxBytes))
                : RemoteResult.Success(new RemoteDocument(call, response.StatusCode, body, response.Content.Headers.ContentType?.MediaType));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return RemoteResult.FailWithError<RemoteDocument>(RemoteFailure.Unreachable, null, call.Error(exception));
        }
    }

    private sealed record RemoteDocument(RemoteCall Call, HttpStatusCode Status, byte[] Body, string? ContentType)
    {
        // Refuses a document that was read, with the exception of its parsing.
        public RemoteResult<T> Refuse<T>(Exception? exception)
            => RemoteResult.FailWithError<T>(RemoteFailure.InvalidResponse, null, Call.Unusable(Status, Body, ContentType, exception));
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

    private sealed record LyricsBody(LyricMetadataBody? Metadata, IReadOnlyList<LyricLineBody?>? Lyrics);

    private sealed record LyricMetadataBody(string? Artist, string? Album, string? Title, string? Author, long? Length, string? By, long? Offset, string? Creator, string? Version, bool? IsSynced);

    private sealed record LyricLineBody(string? Text, long? Start, IReadOnlyList<LyricCueBody?>? Cues);

    private sealed record LyricCueBody(int Position, int EndPosition, long Start, long? End);

    private sealed record SegmentPage(IReadOnlyList<SegmentEntry?>? Items);

    private sealed record SegmentEntry(string? Type, long StartTicks, long EndTicks);

    private sealed record UserResponse(UserPolicyResponse? Policy);

    private sealed record UserPolicyResponse(bool EnableVideoPlaybackTranscoding, bool EnableAudioPlaybackTranscoding, bool EnablePlaybackRemuxing);

    private sealed record EncodingResponse(string? HardwareAccelerationType);
}
