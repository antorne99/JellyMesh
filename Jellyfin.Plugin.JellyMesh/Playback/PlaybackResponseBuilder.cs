using System;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Builds what a client receives from what a remote said. The source is built field by field from a list, instead of
/// cleaning a copy, so that a field added to Jellyfin later cannot carry remote data out.
/// </summary>
public static class PlaybackResponseBuilder
{
    /// <summary>
    /// Builds the media source of a session.
    /// </summary>
    /// <param name="remote">The source from the remote.</param>
    /// <param name="session">The session.</param>
    /// <param name="localToken">The local token.</param>
    /// <returns>The source for the client, null if a remote URL cannot be accepted.</returns>
    public static MediaSourceInfo? Build(MediaSourceInfo remote, PlaybackSession session, string localToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(session);

        var streams = remote.MediaStreams ?? [];
        foreach (var stream in streams)
        {
            stream.Path = null;
            stream.IsExternalUrl = false;
            if (stream.DeliveryUrl is { } url)
            {
                stream.DeliveryUrl = session.MapRootedUrl(url, localToken);
                if (stream.DeliveryUrl is null)
                {
                    return null;
                }
            }
        }

        var container = ContainerOf(remote);
        var isHls = session.Kind == PlaybackStreamKind.Hls;
        return new MediaSourceInfo
        {
            Id = session.Identity.ItemId.ToString("N"),
            Name = remote.Name,
            Protocol = MediaProtocol.Http,
            IsRemote = false,
            Container = remote.Container,
            Size = remote.Size,
            Bitrate = remote.Bitrate,
            RunTimeTicks = remote.RunTimeTicks,
            VideoType = remote.VideoType,
            IsoType = remote.IsoType,
            Video3DFormat = remote.Video3DFormat,
            Timestamp = remote.Timestamp,
            Formats = [],
            MediaStreams = streams,
            MediaAttachments = [],
            DefaultAudioStreamIndex = remote.DefaultAudioStreamIndex,
            DefaultSubtitleStreamIndex = remote.DefaultSubtitleStreamIndex,
            HasSegments = remote.HasSegments,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            TranscodingUrl = isHls ? PlaybackUrls.Master(session.Id, localToken) : PlaybackUrls.Stream(session.Id, container, localToken),
            TranscodingSubProtocol = isHls ? MediaStreamProtocol.hls : MediaStreamProtocol.http,
            TranscodingContainer = isHls ? (IsPlainName(remote.TranscodingContainer) ? remote.TranscodingContainer : null) : container
        };
    }

    /// <summary>
    /// Finds the container to name in a stream URL: a client picks its player from it.
    /// </summary>
    /// <param name="remote">The source from the remote.</param>
    /// <returns>A single lowercase word.</returns>
    public static string ContainerOf(MediaSourceInfo remote)
    {
        ArgumentNullException.ThrowIfNull(remote);

        if (IsPlainName(remote.TranscodingContainer))
        {
            return remote.TranscodingContainer!.ToLowerInvariant();
        }

        var extension = Path.GetExtension(remote.Path)?.TrimStart('.');
        var name = !string.IsNullOrEmpty(extension) ? extension : remote.Container?.Split(',')[0];
        return IsPlainName(name) ? name!.ToLowerInvariant() : "mp4";
    }

    // It goes into a URL of the client, so a word and nothing that could change where that URL leads.
    private static bool IsPlainName(string? name) => name is { Length: > 0 } && name.All(char.IsAsciiLetterOrDigit);
}
