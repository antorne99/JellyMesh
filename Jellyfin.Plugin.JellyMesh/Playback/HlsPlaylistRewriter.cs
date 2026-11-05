using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Rewrites remote HLS playlists, so that a client only sees URLs of its session.
/// </summary>
public static partial class HlsPlaylistRewriter
{
    /// <summary>
    /// Rewrites a playlist.
    /// </summary>
    /// <param name="playlist">The text from the remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="remoteFolder">The folder of the playlist on the remote, ending with a slash.</param>
    /// <param name="prefix">What leads from the client's URL of the playlist to the folder route.</param>
    /// <param name="localToken">The local token.</param>
    /// <param name="secrets">Values of the remote that may not appear in the result.</param>
    /// <returns>The new text, null if some URI cannot be accepted or a secret remains.</returns>
    public static string? Rewrite(string playlist, PlaybackSession session, string remoteFolder, string prefix, string localToken, IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(secrets);

        var failed = false;
        string Replace(string uri)
        {
            var mapped = session.MapRelativeUri(uri, remoteFolder, prefix, localToken);
            failed |= mapped is null;
            return mapped ?? string.Empty;
        }

        var result = new StringBuilder();
        foreach (var line in playlist.Split('\n').Select(line => line.TrimEnd('\r')))
        {
            if (line.StartsWith('#'))
            {
                result.Append(UriAttribute().Replace(line, match => "URI=\"" + Replace(match.Groups[1].Value) + "\""));

                // Whatever is left of the line reaches the client as it came. A URI in any other form would not be mapped.
                failed |= HoldsUri(UriAttribute().Replace(line, string.Empty));
            }
            else if (line.Length > 0)
            {
                result.Append(Replace(line));
            }

            result.Append('\n');
        }

        var text = result.ToString().TrimEnd('\n') + "\n";
        return failed || secrets.Any(secret => secret.Length > 0 && text.Contains(secret, StringComparison.Ordinal)) ? null : text;
    }

    private static bool HoldsUri(string text)
    {
        return text.Contains("://", StringComparison.Ordinal) || text.Contains("URI=", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("URI=\"([^\"]+)\"")]
    private static partial Regex UriAttribute();
}
