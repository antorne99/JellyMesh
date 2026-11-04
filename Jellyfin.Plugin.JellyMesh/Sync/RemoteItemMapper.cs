using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Maps what a remote sent to catalog entries. Copies field by field, so that nothing else can reach the file.
/// </summary>
public static class RemoteItemMapper
{
    private const string VideoStream = "Video";

    /// <summary>
    /// Maps an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="type">The type asked for.</param>
    /// <param name="libraryId">The library it was read from.</param>
    /// <param name="seenUtc">When it was read.</param>
    /// <returns>The entry.</returns>
    public static IndexedItem ToIndexedItem(RemoteItem item, RemoteItemType type, string libraryId, DateTimeOffset seenUtc)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new IndexedItem
        {
            ItemId = item.Id,
            Kind = KindOf(type),
            LibraryId = libraryId,
            LastSeenUtc = seenUtc,
            Name = item.Name,
            OriginalTitle = item.OriginalTitle,
            Overview = item.Overview,
            Taglines = item.Taglines ?? [],
            Genres = item.Genres ?? [],
            Studios = item.Studios?.Select(studio => studio.Name).OfType<string>().ToList() ?? [],
            Tags = item.Tags ?? [],
            People = item.People?.Select(person => new IndexedPerson(person.Name, person.Role, person.Type)).ToList() ?? [],
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            CriticRating = item.CriticRating,
            PremiereDate = item.PremiereDate,
            EndDate = item.EndDate,
            ProductionYear = item.ProductionYear,
            Status = item.Status,
            ProviderIds = ProviderIdsOf(item),
            RunTimeTicks = item.RunTimeTicks,
            DateCreated = item.DateCreated,
            Etag = item.Etag,
            IndexNumber = item.IndexNumber,
            ParentIndexNumber = item.ParentIndexNumber,
            SeriesId = item.SeriesId,
            SeriesName = item.SeriesName,
            SeasonId = item.SeasonId,
            SeasonName = item.SeasonName,
            ImageTags = item.ImageTags ?? new Dictionary<string, string>(),
            BackdropImageTags = item.BackdropImageTags ?? [],
            Versions = item.MediaSources?.Select(ToVersion).ToList() ?? []
        };
    }

    private static IndexedItemKind KindOf(RemoteItemType type) => type switch
    {
        RemoteItemType.Movie => IndexedItemKind.Movie,
        RemoteItemType.Series => IndexedItemKind.Series,
        RemoteItemType.Season => IndexedItemKind.Season,
        RemoteItemType.Episode => IndexedItemKind.Episode,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static Dictionary<string, string> ProviderIdsOf(RemoteItem item)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (provider, value) in item.ProviderIds ?? new Dictionary<string, string?>())
        {
            if (!string.IsNullOrEmpty(value))
            {
                ids[provider] = value;
            }
        }

        return ids;
    }

    private static IndexedVersion ToVersion(RemoteMediaSource source)
    {
        var videos = source.MediaStreams?
            .Where(stream => string.Equals(stream.Type, VideoStream, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var video = videos.Find(stream => stream.IsDefault) ?? videos.FirstOrDefault();

        return new IndexedVersion
        {
            MediaSourceId = source.Id,
            Name = source.Name,
            Container = source.Container,
            Size = source.Size,
            ContainerBitRate = source.Bitrate,
            RunTimeTicks = source.RunTimeTicks,
            ETag = source.ETag,
            Protocol = source.Protocol,
            IsRemote = source.IsRemote,
            Height = video?.Height,
            Width = video?.Width,
            Codec = video?.Codec,
            BitRate = video?.BitRate,
            VideoRange = video?.VideoRange,
            VideoRangeType = video?.VideoRangeType
        };
    }
}
