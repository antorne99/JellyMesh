using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// One call to a remote, as its errors describe it.
/// </summary>
internal sealed class RemoteCall
{
    // Read past the cut, so that a secret across it is still whole when it is removed.
    private const int MarginBytes = 8 * 1024;

    // The headers that tell a Jellyfin server from a proxy in front of it, and a proxy from another.
    private static readonly string[] _tellingHeaders = ["Server", "Via", "Retry-After", "WWW-Authenticate", "Location", "X-Application-Error-Code"];

    private static readonly TimeSpan _bodyLimit = TimeSpan.FromSeconds(2);

    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly string _step;
    private readonly HttpMethod _method;
    private readonly string _url;
    private readonly IReadOnlyCollection<string> _secrets;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteCall"/> class.
    /// </summary>
    /// <param name="step">The name of the step.</param>
    /// <param name="method">The method.</param>
    /// <param name="url">The URL or the path asked.</param>
    /// <param name="secrets">The secrets sent.</param>
    public RemoteCall(string step, HttpMethod method, string url, params string?[] secrets)
    {
        _step = step.EndsWith("Async", StringComparison.Ordinal) ? step[..^"Async".Length] : step;
        _method = method;
        _url = url;
        _secrets = secrets.OfType<string>().ToList();
    }

    /// <summary>
    /// Describes a call that got no answer.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="reason">Why it failed.</param>
    /// <returns>The error.</returns>
    public RemoteError Error(Exception? exception, string? reason = null) => Bare(reason) with
    {
        Exception = exception is null ? null : ErrorText.Clean(exception.ToString(), _secrets)
    };

    /// <summary>
    /// Describes an answer that is refused, with as much of its body as is shown. Never throws.
    /// </summary>
    /// <param name="response">The answer, its body not read yet.</param>
    /// <param name="reason">Why it is refused.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The error.</returns>
    public async Task<RemoteError> ErrorAsync(HttpResponseMessage response, string? reason, CancellationToken cancellationToken)
    {
        var error = Answered(response.StatusCode, response.ReasonPhrase, reason) with
        {
            Answer = AnswerOf(response)
        };

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_bodyLimit);
        var read = new MemoryStream();
        await using (read.ConfigureAwait(false))
        {
            try
            {
                await ReadStartAsync(response.Content, read, limit.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or HttpRequestException)
            {
                error = error with { Exception = ErrorText.Clean(exception.ToString(), _secrets) };
            }

            return error with
            {
                ElapsedMilliseconds = Elapsed(),
                Body = BodyText(read.ToArray(), response.Content.Headers.ContentType?.MediaType)
            };
        }
    }

    /// <summary>
    /// Describes an answer whose body is larger than accepted.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <param name="maxBytes">The most bytes accepted.</param>
    /// <returns>The error.</returns>
    public RemoteError TooLarge(HttpStatusCode status, int maxBytes)
        => Answered(status, null, string.Create(CultureInfo.InvariantCulture, $"The answer is larger than {maxBytes} bytes"));

    /// <summary>
    /// Describes an answer that was read and cannot be used.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <param name="body">The body.</param>
    /// <param name="contentType">The type of the body.</param>
    /// <param name="exception">The exception of its parsing, if any.</param>
    /// <param name="secret">A secret the answer holds, if any.</param>
    /// <returns>The error.</returns>
    public RemoteError Unusable(HttpStatusCode status, byte[] body, string? contentType, Exception? exception, string? secret = null)
    {
        var start = body.AsSpan(0, Math.Min(body.Length, ErrorText.MaxLength + MarginBytes)).ToArray();
        return Answered(status, null, "The answer is not what a Jellyfin server sends here") with
        {
            Answer = OneLine(string.Create(CultureInfo.InvariantCulture, $"{contentType ?? "no type"}; {body.Length} bytes")),
            Body = BodyText(start, contentType, secret),
            Exception = exception is null ? null : ErrorText.Clean(exception.ToString(), WithSecret(secret))
        };
    }

    // Reads the start of a body into a buffer, which keeps what came before a failure.
    private static async Task ReadStartAsync(HttpContent content, MemoryStream buffer, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[16 * 1024];
            int read;
            while (buffer.Length < ErrorText.MaxLength + MarginBytes
                && (read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var kept = (int)Math.Min(read, ErrorText.MaxLength + MarginBytes - buffer.Length);
                await buffer.WriteAsync(chunk.AsMemory(0, kept), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static bool IsText(string? contentType)
    {
        return contentType is null
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase);
    }

    private long Elapsed() => (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

    private IReadOnlyCollection<string> WithSecret(string? secret) => secret is null ? _secrets : [.. _secrets, secret];

    private string OneLine(string text) => ErrorText.Clean(text, _secrets).Replace("\n", " ", StringComparison.Ordinal);

    private RemoteError Bare(string? reason) => new(_step, OneLine(_method + " " + _url), Elapsed()) { Reason = reason };

    private RemoteError Answered(HttpStatusCode status, string? phrase, string? reason) => Bare(reason) with
    {
        Status = OneLine(string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)status} {phrase ?? status.ToString()}"))
    };

    // Describes an answer by its type, its length and the headers that tell who sent it.
    private string AnswerOf(HttpResponseMessage response)
    {
        var parts = new List<string> { response.Content.Headers.ContentType?.ToString() ?? "no type" };
        if (response.Content.Headers.ContentLength is { } length)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{length} bytes"));
        }

        foreach (var name in _tellingHeaders)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                parts.Add(name + ": " + string.Join(", ", values));
            }
        }

        return OneLine(string.Join("; ", parts));
    }

    private string? BodyText(byte[] body, string? contentType, string? secret = null)
    {
        if (body.Length == 0)
        {
            return null;
        }

        return IsText(contentType)
            ? ErrorText.Clean(Encoding.UTF8.GetString(body), WithSecret(secret))
            : "[not shown: it is not text]";
    }
}
