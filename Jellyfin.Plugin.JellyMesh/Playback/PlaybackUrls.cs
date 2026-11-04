using System;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// The URLs clients receive. Each is relative to the server root, because the client prefixes its own address, and
/// each holds the local token, because players send no header.
/// </summary>
public static class PlaybackUrls
{
    /// <summary>
    /// The route of the sessions.
    /// </summary>
    public const string SessionRoute = "JellyMesh/Playback";

    // jellyfin-web retries a failed stream unless it reads both of these in the URL. The retry would loop on a dead
    // remote, and the chain of fallbacks is on the server anyway.
    private const string NoClientRetry = "AllowVideoStreamCopy=false&AllowAudioStreamCopy=false";

    /// <summary>
    /// Gets the path that playlists and subtitles of a session use as a prefix.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The prefix.</returns>
    public static string FolderPrefix(string sessionId) => "/" + SessionRoute + "/" + sessionId + "/f/";

    /// <summary>
    /// Builds the <c>TranscodingUrl</c> of an HLS session.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="localToken">The local token.</param>
    /// <returns>The URL.</returns>
    public static string Master(string sessionId, string localToken)
    {
        return "/" + SessionRoute + "/" + sessionId + "/master.m3u8?PlaySessionId=" + sessionId + "&" + NoClientRetry + "&ApiKey=" + Uri.EscapeDataString(localToken);
    }

    /// <summary>
    /// Builds the <c>TranscodingUrl</c> of a stream session. <c>CopyTimestamps</c> tells jellyfin-web the file keeps its
    /// own times, so that it does not treat the start position as an offset of the stream.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="container">The container.</param>
    /// <param name="localToken">The local token.</param>
    /// <returns>The URL.</returns>
    public static string Stream(string sessionId, string container, string localToken)
    {
        return "/" + SessionRoute + "/" + sessionId + "/stream." + container + "?PlaySessionId=" + sessionId + "&CopyTimestamps=true&" + NoClientRetry + "&ApiKey=" + Uri.EscapeDataString(localToken);
    }

    /// <summary>
    /// Builds the <c>TranscodingUrl</c> of the file of a local copy. A client reads a direct stream by the item it knows,
    /// the stub, so a local copy reaches it only this way.
    /// </summary>
    /// <param name="itemId">The local item.</param>
    /// <param name="mediaSourceId">The media source of the local item.</param>
    /// <param name="container">The container.</param>
    /// <param name="localToken">The local token.</param>
    /// <returns>The URL.</returns>
    public static string LocalFile(Guid itemId, string mediaSourceId, string container, string localToken)
    {
        return "/Videos/" + itemId.ToString("N") + "/stream." + container + "?static=true&MediaSourceId=" + Uri.EscapeDataString(mediaSourceId) + "&CopyTimestamps=true&" + NoClientRetry + "&ApiKey=" + Uri.EscapeDataString(localToken);
    }

    /// <summary>
    /// Adds what stops the retry of jellyfin-web to a local <c>TranscodingUrl</c>.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>The URL with both parameters set to false.</returns>
    public static string WithoutClientRetry(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var kept = url.Split('?', 2) is [var path, var query]
            ? path + "?" + string.Join('&', query.Split('&').Where(pair => !IsCopyParameter(pair)))
            : url + "?";
        return kept.TrimEnd('&') + (kept.EndsWith('?') ? string.Empty : "&") + NoClientRetry;
    }

    /// <summary>
    /// Removes the token from a URL of a remote.
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>The URL without <c>ApiKey</c>.</returns>
    public static string WithoutToken(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (url.Split('?', 2) is not [var path, var query])
        {
            return url;
        }

        var kept = query.Split('&').Where(pair => !IsTokenParameter(pair)).ToArray();
        return kept.Length == 0 ? path : path + "?" + string.Join('&', kept);
    }

    private static bool IsTokenParameter(string pair)
    {
        return pair.StartsWith("ApiKey=", StringComparison.OrdinalIgnoreCase) || pair.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCopyParameter(string pair)
    {
        return pair.StartsWith("AllowVideoStreamCopy=", StringComparison.OrdinalIgnoreCase) || pair.StartsWith("AllowAudioStreamCopy=", StringComparison.OrdinalIgnoreCase);
    }
}
