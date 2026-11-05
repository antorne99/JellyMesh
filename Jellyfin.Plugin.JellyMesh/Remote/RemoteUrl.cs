using System;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Validation of remote base URLs. Whether an address is on the tailnet cannot be told from the URL
/// (MagicDNS short names are plain host names), so it stays the responsibility of the administrator.
/// </summary>
public static class RemoteUrl
{
    // A URI of .NET can hold about 65 kB, and no path of a Jellyfin server comes near this.
    private const int MaxPathLength = 32 * 1024;

    /// <summary>
    /// Normalizes a base URL.
    /// </summary>
    /// <param name="input">The URL as entered.</param>
    /// <param name="normalized">The URL without a trailing slash.</param>
    /// <returns>Whether the URL is an http or https base URL with no credentials, query or fragment.</returns>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    /// <summary>
    /// Builds the URL of a call. Paths of playback come from the answers of the remote, so whatever one holds, the result
    /// stays under the address the administrator entered: an absolute URL, <c>//host</c>, a backslash or <c>..</c> is
    /// refused, never interpreted.
    /// </summary>
    /// <param name="baseUrl">The base URL of the remote, ending with a slash.</param>
    /// <param name="path">The path and query.</param>
    /// <param name="url">The URL.</param>
    /// <returns>Whether the path is acceptable.</returns>
    public static bool TryCombine(Uri baseUrl, string? path, out Uri url)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        url = baseUrl;
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var plain = path.Split('?', 2)[0];
        if (plain.Contains("://", StringComparison.Ordinal)
            || plain.Contains('\\', StringComparison.Ordinal)
            || plain.Any(char.IsControl)
            || plain.Split('/').Any(IsParentSegment))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUrl.AbsoluteUri + path.TrimStart('/'), UriKind.Absolute, out var combined)
            || combined.Scheme != baseUrl.Scheme
            || combined.Port != baseUrl.Port
            || !string.Equals(combined.IdnHost, baseUrl.IdnHost, StringComparison.OrdinalIgnoreCase)
            || !combined.AbsolutePath.StartsWith(baseUrl.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }

        url = combined;
        return true;
    }

    private static bool IsParentSegment(string segment)
    {
        return Uri.UnescapeDataString(segment) == "..";
    }
}
