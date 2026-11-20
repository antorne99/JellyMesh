using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class PhotoMaterializerTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static RemoteItem Album(string cover = "c1") => new()
    {
        Id = "al1",
        Name = "Trips",
        ParentId = TestLibraries.HomeVideos,
        ImageTags = new Dictionary<string, string> { ["Primary"] = cover }
    };

    private static RemoteItem Photo(string id = "p1", string name = "IMG_0001", string file = "IMG_0001.JPG") => new()
    {
        Id = id,
        Name = name,
        ParentId = "al1",
        Path = "/home/Trips/" + file,
        Width = 4000,
        Height = 3000,
        CameraMake = "Canon",
        ImageTags = new Dictionary<string, string> { ["Primary"] = "t" + id }
    };

    private async Task<RemoteServerInfo> AddPhotosAsync(params RemoteItem[] photos)
    {
        _fixture.Sync.Client.Libraries.Add(new(TestLibraries.HomeVideos, "Casa", "homevideos"));
        _fixture.Sync.Client.Add(TestLibraries.HomeVideos, RemoteItemType.PhotoAlbum, Album());
        _fixture.Sync.Client.Add(TestLibraries.HomeVideos, RemoteItemType.Photo, photos.Length == 0 ? [Photo()] : photos);
        return await _fixture.Sync.AddServerAsync("A", TestLibraries.HomeVideos);
    }

    private string AlbumPath(RemoteServerInfo server, params string[] parts)
        => _fixture.PathIn(server, TestLibraries.HomeVideos, ["Video domestici", "Trips", .. parts]);

    [Fact]
    public async Task APhotoBecomesAPlaceholderOfItsFormatNextToTheAlbumCover()
    {
        var a = await AddPhotosAsync();

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(["IMG_0001.jpg", "folder.jpg"], Directory.GetFiles(AlbumPath(a)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(PhotoPlaceholders.For("jpg"), await File.ReadAllBytesAsync(AlbumPath(a, "IMG_0001.jpg")));
        Assert.Equal(["al1/Primary/0"], _fixture.Sync.Client.ImageReads);
        Assert.Equal(StubKind.Photo, Assert.Single(_fixture.Registry.State.Stubs).Kind);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task ANewAlbumCoverReachesTheAlbumAndNotThePhoto()
    {
        var a = await AddPhotosAsync();
        await _fixture.SyncAndMaterializeAsync();
        var photo = new Photo { Id = Guid.NewGuid(), Path = AlbumPath(a, "IMG_0001.jpg"), Name = "IMG_0001" };
        var album = new PhotoAlbum { Id = Guid.NewGuid(), Path = AlbumPath(a), Name = "Trips" };
        _fixture.Library.Setup(library => library.FindByPath(photo.Path, false)).Returns(photo);
        _fixture.Library.Setup(library => library.FindByPath(AlbumPath(a), true)).Returns(album);
        _fixture.Sync.Client.Items[(TestLibraries.HomeVideos, RemoteItemType.PhotoAlbum)][0] = Album("c2");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(AlbumPath(a, "folder.jpg"), album.GetImageInfo(ImageType.Primary, 0)?.Path);
        Assert.Equal(2, _fixture.Sync.Client.ImageReads.Count);
    }

    [Fact]
    public async Task TheNightlyPassLeavesThePlaceholderAsItIs()
    {
        var a = await AddPhotosAsync();
        await _fixture.SyncAndMaterializeAsync();

        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await _fixture.Materializer.MaterializeAsync(true, CancellationToken.None);

        Assert.Equal(PhotoPlaceholders.For("jpg"), await File.ReadAllBytesAsync(AlbumPath(a, "IMG_0001.jpg")));
    }

    private string ThumbnailOfTheStub() => PhotoThumbnails.PathOf(_fixture.Root, Assert.Single(_fixture.Registry.State.Stubs).Id)!;

    // A JPEG with an EXIF segment, as a remote that sent its original file would give.
    private static byte[] WithExif()
    {
        var clean = PhotoPlaceholders.For("jpg");
        return [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x08, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, .. clean[2..]];
    }

    [Fact]
    public async Task APhotoGetsItsThumbnailOutsideTheTypeFoldersBeforeTheScan()
    {
        await AddPhotosAsync();

        await _fixture.SyncAndMaterializeAsync();

        var thumbnail = ThumbnailOfTheStub();
        Assert.Equal(Path.Combine(_fixture.Root, ".jellymesh-thumbs", Assert.Single(_fixture.Registry.State.Stubs).Id + ".jpg"), thumbnail);
        Assert.Equal(_fixture.Sync.Client.Thumbnail, await File.ReadAllBytesAsync(thumbnail));
        Assert.Equal(["p1"], _fixture.Sync.Client.ThumbnailReads);
        Assert.Equal(["al1/Primary/0"], _fixture.Sync.Client.ImageReads);
        _fixture.Library.Verify(library => library.QueueLibraryScan(), Times.Once);
    }

    [Fact]
    public async Task AThumbnailOnDiskIsNotReadAgain()
    {
        await AddPhotosAsync();
        await _fixture.SyncAndMaterializeAsync();

        await _fixture.SyncAndMaterializeAsync();
        await _fixture.Sync.Sync.RunAsync(CancellationToken.None);
        await _fixture.Materializer.MaterializeAsync(true, CancellationToken.None);

        Assert.Single(_fixture.Sync.Client.ThumbnailReads);
    }

    [Fact]
    public async Task AThumbnailWithExifNeverReachesTheDiskAndIsTriedAgain()
    {
        await AddPhotosAsync();
        _fixture.Sync.Client.Thumbnail = WithExif();

        await _fixture.SyncAndMaterializeAsync();

        Assert.False(File.Exists(ThumbnailOfTheStub()));
        Assert.Equal(1, _fixture.Materializer.LastReport?.ImagesPending);
        Assert.DoesNotContain(_fixture.Files, file => file.EndsWith(".tmp", StringComparison.Ordinal));

        _fixture.Sync.Client.Thumbnail = PhotoPlaceholders.For("jpg");
        await _fixture.SyncAndMaterializeAsync();

        Assert.True(File.Exists(ThumbnailOfTheStub()));
    }

    [Fact]
    public async Task AThumbnailReadLaterBecomesThePrimaryOfThePhoto()
    {
        var a = await AddPhotosAsync();
        _fixture.Sync.Client.ThumbnailFailure = RemoteFailure.InvalidResponse;
        await _fixture.SyncAndMaterializeAsync();
        var photo = new Photo { Id = Guid.NewGuid(), Path = AlbumPath(a, "IMG_0001.jpg"), Name = "IMG_0001" };
        _fixture.Library.Setup(library => library.FindByPath(photo.Path, false)).Returns(photo);
        _fixture.Sync.Client.ThumbnailFailure = RemoteFailure.None;

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(ThumbnailOfTheStub(), photo.GetImageInfo(ImageType.Primary, 0)?.Path);
    }

    [Fact]
    public async Task APhotoEditedOnTheRemoteGetsANewThumbnail()
    {
        var a = await AddPhotosAsync();
        await _fixture.SyncAndMaterializeAsync();
        var photo = new Photo { Id = Guid.NewGuid(), Path = AlbumPath(a, "IMG_0001.jpg"), Name = "IMG_0001" };
        _fixture.Library.Setup(library => library.FindByPath(photo.Path, false)).Returns(photo);
        await _fixture.SyncAndMaterializeAsync();
        Assert.Single(_fixture.Sync.Client.ThumbnailReads);

        _fixture.Sync.Client.Items[(TestLibraries.HomeVideos, RemoteItemType.Photo)][0] = Photo() with
        {
            ImageTags = new Dictionary<string, string> { ["Primary"] = "edited" }
        };
        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(2, _fixture.Sync.Client.ThumbnailReads.Count);
        Assert.Equal(ThumbnailOfTheStub(), photo.GetImageInfo(ImageType.Primary, 0)?.Path);
    }

    [Fact]
    public async Task ARemovedPhotoTakesItsThumbnail()
    {
        await AddPhotosAsync();
        await _fixture.SyncAndMaterializeAsync();
        var thumbnail = ThumbnailOfTheStub();

        _fixture.Sync.Client.Items[(TestLibraries.HomeVideos, RemoteItemType.Photo)].Clear();
        await _fixture.SyncAndMaterializeAsync();

        Assert.Empty(_fixture.Registry.State.Stubs);
        Assert.False(File.Exists(thumbnail));
    }

    [Fact]
    public async Task APhotoInAnotherFormatGetsAPlaceholderOfThatFormat()
    {
        var a = await AddPhotosAsync(Photo(file: "IMG_0001.png"));

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal(PhotoPlaceholders.For("png"), await File.ReadAllBytesAsync(AlbumPath(a, "IMG_0001.png")));
    }
}
