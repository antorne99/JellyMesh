using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Reads the YouTube video IDs out of the trailer addresses of a remote, so that no other address is ever kept (decision 55).
/// </summary>
public static partial class YouTubeTrailer
{
    /// <summary>The most trailers kept for one title.</summary>
    public const int MaxTrailers = 20;

    /// <summary>What comes before the ID in the trailer address of an .nfo, the only form Jellyfin reads.</summary>
    public const string KodiPrefix = "plugin://plugin.video.youtube/play/?video_id=";

    /// <summary>What comes before the ID in the trailer address Jellyfin keeps.</summary>
    public const string WatchPrefix = "https://www.youtube.com/watch?v=";

    private static readonly HashSet<string> _watchHosts = new(StringComparer.OrdinalIgnoreCase) { "youtube.com", "www.youtube.com", "m.youtube.com" };

    /// <summary>
    /// Tells whether a text is a YouTube video ID.
    /// </summary>
    /// <param name="id">The text.</param>
    /// <returns>Whether it is one.</returns>
    public static bool IsId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>
    /// Reads the IDs of the trailers that are YouTube videos, once each and in order, and leaves out the rest.
    /// </summary>
    /// <param name="trailers">The trailers the remote sent.</param>
    /// <returns>The IDs.</returns>
    public static IReadOnlyList<string> IdsOf(IReadOnlyList<RemoteMediaUrl?>? trailers)
    {
        if (trailers is null || trailers.Count == 0)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var trailer in trailers)
        {
            if (IdOf(trailer?.Url) is { } id && !ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
                if (ids.Count == MaxTrailers)
                {
                    break;
                }
            }
        }

        return ids;
    }

    private static string? IdOf(string? url)
    {
        if (string.IsNullOrEmpty(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        string? id = null;
        if (string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            id = uri.AbsolutePath.TrimStart('/');
        }
        else if (_watchHosts.Contains(uri.Host) && string.Equals(uri.AbsolutePath, "/watch", StringComparison.Ordinal))
        {
            id = ValueOf(uri.Query, "v");
        }

        return IsId(id) ? id : null;
    }

    private static string? ValueOf(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && string.Equals(pair[..equals], name, StringComparison.Ordinal))
            {
                return pair[(equals + 1)..];
            }
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
