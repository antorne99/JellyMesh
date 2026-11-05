using System;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The address written in a <c>.strm</c> file. It holds no token and no remote address, only the signature of this server.
/// </summary>
public static class PlaybackRoutes
{
    /// <summary>
    /// The path segment of the endpoint a <c>.strm</c> file points to.
    /// </summary>
    public const string PlayPathSegment = "/JellyMesh/Play/";

    /// <summary>
    /// The query parameter of the signature. It stays out of the path, which is all that recognises the endpoint: the
    /// rules on federated items (decision 11, §4.4) do not depend on who signed what.
    /// </summary>
    public const string SignatureParameter = "sig";

    private const int MaxStubIdLength = 64;

    /// <summary>
    /// Builds the content of a <c>.strm</c> file. Always loopback: only the server itself reads it.
    /// </summary>
    /// <param name="httpPort">The HTTP port of the server.</param>
    /// <param name="baseUrl">The base URL of the server, if any.</param>
    /// <param name="stubId">The stub.</param>
    /// <param name="signature">The signature of the stub.</param>
    /// <returns>The URL.</returns>
    public static string StrmUrl(int httpPort, string? baseUrl, string stubId, string signature)
    {
        if (!IsValidStubId(stubId))
        {
            throw new ArgumentException("A stub identifier must be URL-safe.", nameof(stubId));
        }

        var prefix = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : "/" + baseUrl.Trim('/');
        return string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{httpPort}{prefix}{PlayPathSegment}{stubId}?{SignatureParameter}={signature}");
    }

    /// <summary>
    /// Reads the stub out of a <c>.strm</c> URL.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <param name="stubId">The stub, empty when the URL is not one of ours.</param>
    /// <returns>Whether the URL points to the endpoint and names a valid stub.</returns>
    public static bool TryGetStubId(string? url, out string stubId)
    {
        stubId = string.Empty;
        if (PathOf(url) is not { } path)
        {
            return false;
        }

        var start = path.IndexOf(PlayPathSegment, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        var candidate = path[(start + PlayPathSegment.Length)..];
        if (!IsValidStubId(candidate))
        {
            return false;
        }

        stubId = candidate;
        return true;
    }

    /// <summary>
    /// Tells whether a URL points to the endpoint of a JellyMesh server, whatever its host, port or base URL.
    /// Decision 11: an item with such a URL is itself federated on that server, so it is never imported.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>Whether it points to the endpoint.</returns>
    public static bool IsPlaybackEndpoint(string? url)
    {
        return PathOf(url)?.Contains(PlayPathSegment, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? PathOf(string? url)
    {
        // On Linux a rooted file path parses as an absolute file URI.
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsolutePath
            : null;
    }

    private static bool IsValidStubId(string? stubId)
    {
        return !string.IsNullOrEmpty(stubId)
            && stubId.Length <= MaxStubIdLength
            && stubId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}
