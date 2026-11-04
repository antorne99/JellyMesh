using System;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// Validation of remote base URLs. Whether an address is on the tailnet cannot be told from the URL
/// (MagicDNS short names are plain host names), so it stays the responsibility of the administrator.
/// </summary>
public static class RemoteUrl
{
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
}
