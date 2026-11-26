using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class TypeNfoTests
{
    private static readonly IndexedItem _musicVideo = new()
    {
        ItemId = "mv",
        Kind = IndexedItemKind.MusicVideo,
        LibraryId = TestLibraries.MusicVideos,
        LastSeenUtc = DateTimeOffset.UnixEpoch,
        Name = "Song (2019)",
        ProductionYear = 2019,
        Overview = "Plot",
        Genres = ["Pop"],
        Artists = ["First", "Second"],
        Album = "Album",
        PremiereDate = new DateTimeOffset(2019, 5, 6, 0, 0, 0, TimeSpan.Zero),
        ProviderIds = new Dictionary<string, string> { ["Imvdb"] = "7" }
    };

    private static readonly IndexedItem _homeVideo = new()
    {
        ItemId = "hv",
        Kind = IndexedItemKind.Video,
        LibraryId = TestLibraries.HomeVideos,
        LastSeenUtc = DateTimeOffset.UnixEpoch,
        Name = "Beach day",
        Overview = "At the sea",
        Tags = ["Summer"],
        PremiereDate = new DateTimeOffset(2021, 7, 8, 0, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public void AProtectedMusicVideoHasItsArtistsAlbumAndLockdata()
    {
        var root = XDocument.Parse(NfoDocument.MusicVideo(StubMetadata.From(_musicVideo, true))).Root!;

        Assert.Equal("musicvideo", root.Name.LocalName);
        Assert.Equal("Song (2019)", root.Element("title")?.Value);
        Assert.Equal(["First", "Second"], root.Elements("artist").Select(artist => artist.Value));
        Assert.Equal("Album", root.Element("album")?.Value);
        Assert.Equal("Plot", root.Element("plot")?.Value);
        Assert.Equal("2019-05-06", root.Element("premiered")?.Value);
        Assert.Equal("true", root.Element("lockdata")?.Value);
        Assert.Equal("7", root.Element("uniqueid")?.Value);
    }

    [Fact]
    public void AnUnprotectedMusicVideoKeepsItsArtistsAndAlbumWithoutDetailsOrLock()
    {
        var root = XDocument.Parse(NfoDocument.MusicVideo(StubMetadata.From(_musicVideo, false))).Root!;

        Assert.Equal(["First", "Second"], root.Elements("artist").Select(artist => artist.Value));
        Assert.Equal("Album", root.Element("album")?.Value);
        Assert.Null(root.Element("plot"));
        Assert.Null(root.Element("lockdata"));
    }

    [Fact]
    public void AProtectedHomeVideoIsAMovieDocumentWithItsDateAndLockdata()
    {
        var root = XDocument.Parse(NfoDocument.HomeVideo(StubMetadata.From(_homeVideo, true))).Root!;

        Assert.Equal("movie", root.Name.LocalName);
        Assert.Equal("Beach day", root.Element("title")?.Value);
        Assert.Equal("At the sea", root.Element("plot")?.Value);
        Assert.Equal("Summer", root.Element("tag")?.Value);
        Assert.Equal("2021-07-08", root.Element("premiered")?.Value);
        Assert.Equal("true", root.Element("lockdata")?.Value);
        Assert.Null(root.Element("artist"));
    }

    [Fact]
    public void AnUnprotectedHomeVideoStillCopiesItsDate()
    {
        var root = XDocument.Parse(NfoDocument.HomeVideo(StubMetadata.From(_homeVideo, false))).Root!;

        Assert.Equal("2021-07-08", root.Element("premiered")?.Value);
        Assert.Null(root.Element("plot"));
        Assert.Null(root.Element("lockdata"));
    }

    [Fact]
    public void ArtistsAndAlbumAreReadOnlyFromMusicVideos()
    {
        var film = _musicVideo with { Kind = IndexedItemKind.Movie };

        var metadata = StubMetadata.From(film, true);

        Assert.Empty(metadata.Artists);
        Assert.Null(metadata.Album);
    }
}
