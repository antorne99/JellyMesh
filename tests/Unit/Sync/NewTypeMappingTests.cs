using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

// What the index keeps of the new types (§3.12, 3.13), from answers shaped like those of 12.4 (F48).
public class NewTypeMappingTests
{
    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };

    private static IndexedItem Map(string json, RemoteItemType type)
        => RemoteItemMapper.ToIndexedItem(JsonSerializer.Deserialize<RemoteItem>(json, _options)!, type, "lib", DateTimeOffset.UnixEpoch);

    private const string Track = """
        {
          "Id": "t1", "Name": "Track 1", "Type": "Audio", "Path": "/music/F48 Artist/F48 Album (2020)/01 - Track 1.flac",
          "ParentId": "album1", "Album": "F48 Album", "AlbumId": "album1", "AlbumPrimaryImageTag": "tag1",
          "Artists": ["F48 Artist", "Guest"], "AlbumArtist": "F48 Artist", "AlbumArtists": [{ "Name": "F48 Artist", "Id": "artist1" }],
          "IndexNumber": 1, "ParentIndexNumber": 2, "ProductionYear": 2020, "Genres": ["Rock"], "RunTimeTicks": 60000000,
          "HasLyrics": true,
          "ProviderIds": { "MusicBrainzRecording": "rec", "MusicBrainzTrack": "trk", "MusicBrainzAlbum": "alb" },
          "MediaSources": [{ "Id": "s1", "Container": "flac", "Bitrate": 1411000,
            "MediaStreams": [{ "Type": "Audio", "Codec": "flac", "BitDepth": 24, "SampleRate": 96000, "Channels": 2, "BitRate": 1411000 }] }]
        }
        """;

    [Fact]
    public void ATrackKeepsItsAlbumArtistsNumbersMusicBrainzIdsLyricsAndAudioStream()
    {
        var track = Map(Track, RemoteItemType.Audio);

        Assert.Equal(IndexedItemKind.Audio, track.Kind);
        Assert.Equal("F48 Album", track.Album);
        Assert.Equal("album1", track.AlbumId);
        Assert.Equal("tag1", track.AlbumPrimaryImageTag);
        Assert.Equal(["F48 Artist", "Guest"], track.Artists!);
        Assert.Equal(["F48 Artist"], track.AlbumArtists!);
        Assert.Equal((1, 2, 2020), (track.IndexNumber, track.ParentIndexNumber, track.ProductionYear));
        Assert.Equal(["Rock"], track.Genres);
        Assert.Equal(60000000, track.RunTimeTicks);
        Assert.True(track.HasLyrics);
        Assert.Equal("rec", track.ProviderIds["MusicBrainzRecording"]);
        Assert.Equal("trk", track.ProviderIds["MusicBrainzTrack"]);
        var stream = Assert.Single(Assert.Single(track.Versions).Streams);
        Assert.Equal(("flac", 24, 96000, 2), (stream.Codec, stream.BitDepth, stream.SampleRate, stream.Channels));
        Assert.Null(track.Extension);
        Assert.Null(track.Photo);
    }

    [Fact]
    public void AnAlbumArtistAloneStandsForTheArtistsOfTheAlbum()
    {
        var track = Map("""{ "Id": "t1", "AlbumArtist": "Solo" }""", RemoteItemType.Audio);

        Assert.Equal(["Solo"], track.AlbumArtists!);
    }

    [Fact]
    public void AMusicVideoKeepsItsArtistsAndAlbum()
    {
        var clip = Map("""{ "Id": "v1", "Name": "Clip", "Artists": ["F48 Artist"], "Album": "F48 Album" }""", RemoteItemType.MusicVideo);

        Assert.Equal(IndexedItemKind.MusicVideo, clip.Kind);
        Assert.Equal(["F48 Artist"], clip.Artists!);
        Assert.Equal("F48 Album", clip.Album);
    }

    private const string Photo = """
        {
          "Id": "p1", "Name": "IMG_0001", "Type": "Photo", "Path": "/secret/home/Album F48/IMG_0001.JPG", "ParentId": "album",
          "Album": "Album F48", "Width": 640, "Height": 480,
          "CameraMake": "F48Make", "CameraModel": "F48 Model X", "Software": "Editor 1", "ExposureTime": 0.004, "FocalLength": 35,
          "Aperture": 2.8, "ShutterSpeed": 7.97, "ImageOrientation": "RightTop", "IsoSpeedRating": 200,
          "Latitude": 45.45, "Longitude": 9.183333333333334, "Altitude": 120.5,
          "PremiereDate": "2020-07-01T10:34:56.0000000Z"
        }
        """;

    [Fact]
    public void APhotoKeepsItsSizeAndExif()
    {
        var photo = Map(Photo, RemoteItemType.Photo);

        Assert.Equal(IndexedItemKind.Photo, photo.Kind);
        Assert.Equal("album", photo.ParentId);
        Assert.Equal("Album F48", photo.Album);
        Assert.Equal(new IndexedPhoto
        {
            Width = 640,
            Height = 480,
            CameraMake = "F48Make",
            CameraModel = "F48 Model X",
            Software = "Editor 1",
            ExposureTime = 0.004,
            FocalLength = 35,
            Aperture = 2.8,
            ShutterSpeed = 7.97,
            ImageOrientation = "RightTop",
            IsoSpeedRating = 200
        }, photo.Photo);
        Assert.Equal(new DateTimeOffset(2020, 7, 1, 10, 34, 56, TimeSpan.Zero), photo.PremiereDate);
    }

    [Fact]
    public void APhotoNeverKeepsWhereItWasTaken()
    {
        var json = JsonSerializer.Serialize(Map(Photo, RemoteItemType.Photo));

        Assert.DoesNotContain("45.45", json, StringComparison.Ordinal);
        Assert.DoesNotContain("9.18", json, StringComparison.Ordinal);
        Assert.DoesNotContain("120.5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Latitude", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Longitude", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Altitude", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/media/books/F48 Author/F48 Epub.epub", "epub")]
    [InlineData("/media/books/F48 Author/F48 Comic.CBZ", "cbz")]
    [InlineData("D:\\Libri\\Autore\\Libro.azw3", "azw3")]
    [InlineData("/media/books/a.b/Libro.pdf", "pdf")]
    [InlineData("/media/books/Libro.mobi", "mobi")]
    [InlineData("/media/books/Libro.azw", "azw")]
    [InlineData("/media/books/Libro.cbr", "cbr")]
    [InlineData("/media/books/Libro.cb7", "cb7")]
    [InlineData("/media/books/Libro.cbt", "cbt")]
    [InlineData("/media/books/Libro.strm", null)]
    [InlineData("/media/books/Libro.nfo", null)]
    [InlineData("/media/books/Libro.html", null)]
    [InlineData("/media/books/Libro.jpg", null)]
    [InlineData("/media/books/x.epub?k=a.b12c", null)]
    [InlineData("/media/books/Libro", null)]
    [InlineData("/media/books.d/Libro", null)]
    [InlineData("/media/books/Libro.", null)]
    [InlineData("/media/books/Libro.toolong", null)]
    [InlineData("/media/books/Libro.e/pub", null)]
    [InlineData("/media/books/Libro.ep ub", null)]
    [InlineData("/media/books/Libro.èpub", null)]
    [InlineData("http://remote.invalid:8096/Items/1/File?api_key=tok3n0123456789sentinel", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ABookKeepsOnlyTheExtensionOfItsFile(string? path, string? extension)
    {
        var book = RemoteItemMapper.ToIndexedItem(new RemoteItem { Id = "b1", Path = path }, RemoteItemType.Book, "lib", DateTimeOffset.UnixEpoch);

        Assert.Equal(IndexedItemKind.Book, book.Kind);
        Assert.Equal(extension, book.Extension);
        var json = JsonSerializer.Serialize(book);
        Assert.DoesNotContain("/media", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Libri", json, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/srv/federata/0f8fad5b-d9cb-469f-a165-70867728950e/4c1e9b0a5d2f4e3b8a7c6d5e4f3a2b1c/Libri/Dune (1965)/Dune (1965).epub")]
    [InlineData("D:\\Federata\\0F8FAD5B-D9CB-469F-A165-70867728950E\\4c1e9b0a5d2f4e3b8a7c6d5e4f3a2b1c\\Libri\\Dune\\Dune.pdf")]
    [InlineData("/0f8fad5b-d9cb-469f-a165-70867728950e/4c1e9b0a5d2f4e3b8a7c6d5e4f3a2b1c/Libri/Dune.cbz")]
    public void ABookShapedAsAPlaceholderOfAnotherJellyMeshGetsNoFile(string path)
    {
        var book = RemoteItemMapper.ToIndexedItem(new RemoteItem { Id = "b1", Path = path }, RemoteItemType.Book, "lib", DateTimeOffset.UnixEpoch);

        Assert.Null(book.Extension);
    }

    [Theory]
    [InlineData("/media/0f8fad5b-d9cb-469f-a165-70867728950e/4c1e9b0a5d2f4e3b8a7c6d5e4f3a2b1c/Film/Dune.epub")]
    [InlineData("/media/books/4c1e9b0a5d2f4e3b8a7c6d5e4f3a2b1c/Libri/Dune.epub")]
    [InlineData("/media/0f8fad5b-d9cb-469f-a165-70867728950e/books/Libri/Dune.epub")]
    [InlineData("/media/0f8fad5b-d9cb-469f-a165-70867728950e/4c1e9b0a5d2f4e3b8a7c6d5e4f3a2b1c/libri/Dune.epub")]
    [InlineData("/media/0f8fad5b-d9cb-469f-a165-70867728950e/4c1e9b0a5d2f4e3b8a7c6d5e4f3a2b1c/Libri.epub")]
    public void ABookOnlyPartlyShapedAsAPlaceholderKeepsItsFile(string path)
    {
        var book = RemoteItemMapper.ToIndexedItem(new RemoteItem { Id = "b1", Path = path }, RemoteItemType.Book, "lib", DateTimeOffset.UnixEpoch);

        Assert.Equal("epub", book.Extension);
    }

    [Theory]
    [InlineData("JellyMesh", "0123")]
    [InlineData("jellymesh", "0123")]
    [InlineData("JellyMesh", "")]
    [InlineData("JellyMesh", null)]
    public void ABookWithTheMarkOfAnotherJellyMeshGetsNoFile(string key, string? value)
    {
        var item = new RemoteItem { Id = "b1", Path = "/media/books/Dune.epub", ProviderIds = new Dictionary<string, string?> { [key] = value, ["Isbn"] = "1" } };

        var book = RemoteItemMapper.ToIndexedItem(item, RemoteItemType.Book, "lib", DateTimeOffset.UnixEpoch);

        Assert.Null(book.Extension);
    }

    [Fact]
    public void APhotoKeepsOnlyTheExtensionOfItsFile()
    {
        var photo = Map(Photo, RemoteItemType.Photo);

        Assert.Equal("jpg", photo.Extension);
        Assert.DoesNotContain("/secret", JsonSerializer.Serialize(photo), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a.jpeg", "jpeg")]
    [InlineData("a.PNG", "png")]
    [InlineData("a.gif", "gif")]
    [InlineData("a.webp", "webp")]
    [InlineData("a.bmp", "bmp")]
    [InlineData("a.tif", "tif")]
    [InlineData("a.tiff", "tiff")]
    [InlineData("a.heic", "heic")]
    [InlineData("a.heif", "heif")]
    [InlineData("a.avif", "avif")]
    [InlineData("a.dng", "dng")]
    [InlineData("a.cr2", "cr2")]
    [InlineData("a.nef", "nef")]
    [InlineData("a.arw", "arw")]
    [InlineData("a.svg", null)]
    [InlineData("a.strm", null)]
    [InlineData("a.nfo", null)]
    [InlineData("a.html", null)]
    [InlineData("a.epub", null)]
    public void APhotoKeepsOnlyAnImageExtension(string name, string? extension)
    {
        var photo = RemoteItemMapper.ToIndexedItem(new RemoteItem { Id = "p1", Path = "/home/" + name }, RemoteItemType.Photo, "lib", DateTimeOffset.UnixEpoch);

        Assert.Equal(extension, photo.Extension);
    }

    [Theory]
    [InlineData(RemoteItemType.Video)]
    [InlineData(RemoteItemType.Folder)]
    [InlineData(RemoteItemType.PhotoAlbum)]
    [InlineData(RemoteItemType.AudioBook)]
    public void AnItemOfTheNewTypesKeepsItsParentAndNoExtensionOutsideBooksAndPhotos(RemoteItemType type)
    {
        var item = Map("""{ "Id": "i1", "Name": "Day 1", "ParentId": "trip", "Path": "/secret/home/2020/Trip/Day 1/clip1.mkv" }""", type);

        Assert.Equal(type.ToString(), item.Kind.ToString());
        Assert.Equal("trip", item.ParentId);
        Assert.Null(item.Extension);
        Assert.Null(item.Photo);
    }

    [Theory]
    [InlineData(RemoteItemType.Movie)]
    [InlineData(RemoteItemType.Series)]
    [InlineData(RemoteItemType.Season)]
    [InlineData(RemoteItemType.Episode)]
    public void AFilmOrASeriesEntryKeepsNoneOfTheNewFieldsSoItsStoredFormIsUnchanged(RemoteItemType type)
    {
        var item = Map(Photo, type);
        var plain = RemoteItemMapper.ToIndexedItem(new RemoteItem { Id = "p1", Name = "IMG_0001", PremiereDate = new DateTimeOffset(2020, 7, 1, 10, 34, 56, TimeSpan.Zero) }, type, "lib", DateTimeOffset.UnixEpoch);

        Assert.Equal(JsonSerializer.Serialize(plain), JsonSerializer.Serialize(item));
    }
}
