using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

internal static class TestItems
{
    public static RemoteItem Movie(string id, string name = "Film", int year = 2020, string tmdb = "1") => new()
    {
        Id = id,
        Name = name,
        ProductionYear = year,
        ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = tmdb },
        MediaSources = [Source(id, 1080, 5_000_000)]
    };

    public static RemoteItem Series(string id, string name = "Show", int year = 2020) => new()
    {
        Id = id,
        Name = name,
        ProductionYear = year,
        ProviderIds = new Dictionary<string, string?> { ["Tvdb"] = "71", ["Imdb"] = "tt71", ["Tmdb"] = "72", ["Custom"] = "c71" }
    };

    public static RemoteItem Season(string id, string seriesId, int number) => new()
    {
        Id = id,
        Name = string.Create(CultureInfo.InvariantCulture, $"Season {number}"),
        IndexNumber = number,
        SeriesId = seriesId
    };

    public static RemoteItem Episode(string id, string seriesId, int season, int number, string seriesName = "Show") => new()
    {
        Id = id,
        Name = "Episode",
        ParentIndexNumber = season,
        IndexNumber = number,
        SeriesId = seriesId,
        SeriesName = seriesName,
        ProviderIds = new Dictionary<string, string?>(),
        MediaSources = [Source(id, 720, 2_000_000)]
    };

    public static RemoteMediaSource Source(string id, int height, int bitRate, string codec = "h264", string range = "SDR") => new()
    {
        Id = id,
        Name = "Version",
        Container = "mkv",
        MediaStreams =
        [
            new RemoteStreamInfo("Audio", "aac", null, null, 192000, null, null, true),
            new RemoteStreamInfo("Video", codec, height, height * 16 / 9, bitRate, range, range, true)
        ]
    };

    public static RemoteItem[] Movies(int count, string prefix = "m")
        => Enumerable.Range(0, count)
            .Select(index => Movie(prefix + index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
}
