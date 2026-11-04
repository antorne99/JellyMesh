using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dlna;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Reads a master playlist. A remote that may not transcode says so nowhere: it serves the source codec anyway, and only
/// the <c>CODECS</c> attribute shows it.
/// </summary>
public static partial class HlsMaster
{
    private static readonly IReadOnlyDictionary<string, string> _codecNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["avc1"] = "h264",
        ["avc3"] = "h264",
        ["hvc1"] = "hevc",
        ["hev1"] = "hevc",
        ["dvh1"] = "hevc",
        ["dvhe"] = "hevc",
        ["av01"] = "av1",
        ["vp09"] = "vp9",
        ["vp8"] = "vp8",
        ["mp4a"] = "aac",
        ["ac-3"] = "ac3",
        ["ec-3"] = "eac3",
        ["flac"] = "flac",
        ["opus"] = "opus",
        ["mp3"] = "mp3",
        ["vorbis"] = "vorbis"
    };

    private static readonly HashSet<string> _videoCodecs = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc", "av1", "vp9", "vp8" };

    /// <summary>
    /// Lists the variants of a master playlist.
    /// </summary>
    /// <param name="master">The text.</param>
    /// <returns>The URI and the codecs of each variant.</returns>
    public static IReadOnlyList<HlsVariant> ReadVariants(string master)
    {
        ArgumentNullException.ThrowIfNull(master);

        var variants = new List<HlsVariant>();
        var lines = master.Split('\n').Select(line => line.Trim()).ToList();
        for (var index = 0; index < lines.Count - 1; index++)
        {
            if (!lines[index].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                continue;
            }

            var codecs = CodecsAttribute().Match(lines[index]);
            var uri = lines.Skip(index + 1).FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
            if (uri is not null)
            {
                variants.Add(new HlsVariant(uri, codecs.Success ? codecs.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : []));
            }
        }

        return variants;
    }

    /// <summary>
    /// Finds the first segment of a variant playlist.
    /// </summary>
    /// <param name="variant">The text.</param>
    /// <returns>The URI, null if there is none.</returns>
    public static string? FirstSegment(string variant)
    {
        ArgumentNullException.ThrowIfNull(variant);

        return variant.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#'));
    }

    /// <summary>
    /// Tells whether a client can decode a variant, going by the HLS transcoding profiles it declares.
    /// </summary>
    /// <param name="variant">The variant.</param>
    /// <param name="profile">The profile of the client, null if it sent none.</param>
    /// <returns>False only if the variant holds a known codec the profile does not list.</returns>
    public static bool IsPlayable(HlsVariant variant, DeviceProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(variant);

        var hls = profile?.TranscodingProfiles?
            .Where(candidate => candidate.Type == DlnaProfileType.Video && candidate.Protocol == MediaStreamProtocol.hls)
            .ToList();
        if (hls is not { Count: > 0 })
        {
            return true;
        }

        var video = Names(hls.Select(candidate => candidate.VideoCodec));
        var audio = Names(hls.Select(candidate => candidate.AudioCodec));
        foreach (var codec in variant.Codecs)
        {
            if (!_codecNames.TryGetValue(codec.Split('.')[0], out var name))
            {
                continue;
            }

            var allowed = _videoCodecs.Contains(name) ? video : audio;
            if (allowed.Count > 0 && !allowed.Contains(name))
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<string> Names(IEnumerable<string?> lists)
    {
        return lists
            .Where(list => !string.IsNullOrEmpty(list))
            .SelectMany(list => list!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("CODECS=\"([^\"]*)\"")]
    private static partial Regex CodecsAttribute();
}
