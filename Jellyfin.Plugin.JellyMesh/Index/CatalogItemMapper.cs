using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// Maps catalog entries to the items <see cref="DedupEngine"/> works on.
/// </summary>
public static class CatalogItemMapper
{
    private const string HdrRange = "HDR";
    private const string SdrRange = "SDR";
    private const string AudioStream = "Audio";
    private const string MusicBrainzRecording = "MusicBrainzRecording";
    private const string MusicBrainzTrack = "MusicBrainzTrack";

    /// <summary>
    /// Gets the films, music videos and episodes of a server. An episode takes title, year and IDs from its series, because Jellyfin
    /// sends none on episodes. If the series is not in the catalog it keeps only the series name, so it is not hidden.
    /// </summary>
    /// <param name="catalog">The catalog of the server.</param>
    /// <returns>The items.</returns>
    public static IReadOnlyList<CatalogItem> ToCatalogItems(ServerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var serverId = catalog.ServerId.ToString("N");
        var series = catalog.Items
            .Where(item => item.Kind == IndexedItemKind.Series)
            .DistinctBy(item => item.ItemId)
            .ToDictionary(item => item.ItemId, StringComparer.Ordinal);

        return catalog.Items
            .Where(item => item.Kind is IndexedItemKind.Movie or IndexedItemKind.Episode or IndexedItemKind.MusicVideo)
            .Select(item => ToCatalogItem(item, serverId, series))
            .ToList();
    }

    private static CatalogItem ToCatalogItem(IndexedItem item, string serverId, Dictionary<string, IndexedItem> series)
    {
        var versions = item.Versions.Select(ToMediaVersion).ToList();
        if (item.Kind is IndexedItemKind.Movie or IndexedItemKind.MusicVideo)
        {
            return new CatalogItem
            {
                ServerId = serverId,
                ItemId = item.ItemId,
                Kind = item.Kind == IndexedItemKind.Movie ? ContentKind.Movie : ContentKind.MusicVideo,
                Artists = ArtistsOf(item.Artists ?? []),
                Title = TitleOf(item.Name),
                Year = item.ProductionYear,
                Ids = ContentIds.FromProviderIds(item.ProviderIds),
                Metadata = new ComparableMetadata
                {
                    OriginalTitle = item.OriginalTitle,
                    RunTimeTicks = item.RunTimeTicks,
                    Overview = item.Overview,
                    Genres = item.Genres,
                    Studios = item.Studios,
                    People = new Lazy<IReadOnlyList<string>>(item.People.Select(person => person.Name).OfType<string>().ToList())
                },
                Versions = versions
            };
        }

        var parent = item.SeriesId is { } seriesId ? series.GetValueOrDefault(seriesId) : null;
        return new CatalogItem
        {
            ServerId = serverId,
            ItemId = item.ItemId,
            Kind = ContentKind.Episode,
            SeriesId = item.SeriesId,
            Title = TitleOf(parent?.Name ?? item.SeriesName),
            Year = parent?.ProductionYear,
            Ids = ContentIds.FromProviderIds(parent?.ProviderIds),
            SeasonNumber = item.ParentIndexNumber,
            EpisodeNumber = item.IndexNumber,
            EpisodeNumberEnd = item.IndexNumberEnd,
            Versions = versions
        };
    }

    /// <summary>
    /// Gets the music tracks of a server, as <see cref="TrackDedup"/> compares them (row 453).
    /// </summary>
    /// <param name="catalog">The catalog of the server.</param>
    /// <returns>The tracks.</returns>
    public static IReadOnlyList<TrackItem> ToTrackItems(ServerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var serverId = catalog.ServerId.ToString("N");
        return catalog.Items
            .Where(item => item.Kind == IndexedItemKind.Audio)
            .Select(item => new TrackItem
            {
                ServerId = serverId,
                ItemId = item.ItemId,
                Title = item.Name,
                AlbumArtists = item.AlbumArtists ?? [],
                Artists = item.Artists ?? [],
                Album = item.Album,
                DiscNumber = item.ParentIndexNumber,
                TrackNumber = item.IndexNumber,
                RunTimeTicks = item.RunTimeTicks,
                RecordingId = RecordingIdOf(item.ProviderIds),
                TrackId = TrackIdOf(item.ProviderIds),
                Versions = item.Versions.Select(version => new MediaVersion(version.MediaSourceId, AudioQualityOf(version))).ToList()
            })
            .ToList();
    }

    /// <summary>
    /// Gets the quality of a track version from its default audio stream, else its first, with the container bitrate when
    /// the stream has none (D65).
    /// </summary>
    /// <param name="version">The version.</param>
    /// <returns>The quality.</returns>
    public static SourceQuality AudioQualityOf(IndexedVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var audio = version.Streams.Where(stream => string.Equals(stream.Type, AudioStream, StringComparison.OrdinalIgnoreCase)).ToList();
        var stream = audio.FirstOrDefault(stream => stream.IsDefault) ?? audio.FirstOrDefault();
        return SourceQuality.Audio(stream?.Codec, stream?.BitDepth, stream?.SampleRate, stream?.BitRate ?? version.ContainerBitRate);
    }

    /// <summary>
    /// Gets the quality of a version, as the dedup compares it.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <returns>The quality.</returns>
    public static SourceQuality QualityOf(IndexedVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var isHdr = string.Equals(version.VideoRange, HdrRange, StringComparison.OrdinalIgnoreCase);
        var isSdr = string.Equals(version.VideoRange, SdrRange, StringComparison.OrdinalIgnoreCase);
        return new SourceQuality(version.Height, isHdr, version.BitRate, version.Codec) { IsRangeKnown = isHdr || isSdr };
    }

    // A name that may have been cut is no title to match by, so that the item matches by its IDs only (row 529).
    private static string? TitleOf(string? name) => RemoteName.MayBeCut(name) ? null : name;

    // Gets no artist at all when one of them may have been cut, as TitleOf.
    private static IReadOnlyList<string> ArtistsOf(IReadOnlyList<string> artists) => artists.Any(RemoteName.MayBeCut) ? [] : artists;

    private static MediaVersion ToMediaVersion(IndexedVersion version) => new(version.MediaSourceId, QualityOf(version));

    /// <summary>Gets the MusicBrainz recording ID of a track.</summary>
    /// <param name="providerIds">The provider IDs of the track.</param>
    /// <returns>The ID, or null.</returns>
    internal static string? RecordingIdOf(IEnumerable<KeyValuePair<string, string>> providerIds) => ProviderId(providerIds, MusicBrainzRecording);

    /// <summary>Gets the MusicBrainz track ID of a track.</summary>
    /// <param name="providerIds">The provider IDs of the track.</param>
    /// <returns>The ID, or null.</returns>
    internal static string? TrackIdOf(IEnumerable<KeyValuePair<string, string>> providerIds) => ProviderId(providerIds, MusicBrainzTrack);

    private static string? ProviderId(IEnumerable<KeyValuePair<string, string>> providerIds, string name) =>
        providerIds.FirstOrDefault(entry => string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
