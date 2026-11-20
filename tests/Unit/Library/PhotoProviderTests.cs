using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The stub providers on a photo: the placeholder gives Jellyfin nothing, so size and EXIF come from the plugin (D7, D45).
public sealed class PhotoProviderTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public PhotoProviderTests() => _fixture.Sync.Client.Libraries.Add(new RemoteLibrary(TestLibraries.HomeVideos, "Casa", "homevideos"));

    public void Dispose() => _fixture.Dispose();

    private StubBeforeProbeProvider Before() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private StubAfterProbeProvider After() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private StubPhotoProvider AfterPhotoProvider() => new(_fixture.Registry, _fixture.Sync.Store, _fixture.Technical);

    private static MetadataRefreshOptions Options() => new(new Mock<IDirectoryService>().Object);

    private async Task<string> PhotoStubAsync()
    {
        _fixture.Sync.Client.Add(TestLibraries.HomeVideos, RemoteItemType.PhotoAlbum, new RemoteItem { Id = "al1", Name = "Trips", ParentId = TestLibraries.HomeVideos });
        _fixture.Sync.Client.Add(TestLibraries.HomeVideos, RemoteItemType.Photo, new RemoteItem
        {
            Id = "p1",
            Name = "IMG_0001",
            ParentId = "al1",
            Path = "/home/Trips/IMG_0001.jpg",
            Overview = "Sea",
            Width = 4000,
            Height = 3000,
            CameraMake = "Canon",
            CameraModel = "R6",
            Software = "Editor 1",
            ExposureTime = 0.004,
            FocalLength = 35,
            Aperture = 2.8,
            ShutterSpeed = 7.97,
            ImageOrientation = "RightTop",
            IsoSpeedRating = 200,
            ProviderIds = new Dictionary<string, string?> { ["Custom"] = "c1" }
        });
        var a = await _fixture.Sync.AddServerAsync("A", TestLibraries.HomeVideos);
        await _fixture.SyncAndMaterializeAsync();
        return _fixture.PathIn(a, TestLibraries.HomeVideos, "Video domestici", "Trips", "IMG_0001.jpg");
    }

    private string Thumbnail() => PhotoThumbnails.PathOf(_fixture.Root, Assert.Single(_fixture.Registry.State.Stubs).Id)!;

    private static Photo Located(string path) => new()
    {
        Id = Guid.NewGuid(),
        Path = path,
        Name = "IMG_0001",
        Latitude = 1,
        Longitude = 2,
        Altitude = 3
    };

    private void AssertFromTheRemote(Photo item, bool isProtected)
    {
        Assert.Equal(("IMG_0001", "Sea", 4000, 3000), (item.Name, item.Overview, item.Width, item.Height));
        Assert.Equal(("Canon", "R6", "Editor 1", 200), (item.CameraMake, item.CameraModel, item.Software, item.IsoSpeedRating));
        Assert.Equal((0.004, 35.0, 2.8, 7.97), (item.ExposureTime, item.FocalLength, item.Aperture, item.ShutterSpeed));
        Assert.Equal(ImageOrientation.RightTop, item.Orientation);
        Assert.Equal(((double?)null, (double?)null, (double?)null), (item.Latitude, item.Longitude, item.Altitude));
        Assert.Equal("c1", item.GetProviderId("Custom"));
        Assert.Equal(Assert.Single(_fixture.Registry.State.Stubs).Id, item.GetProviderId(FederatedPlaceholderMark.ProviderId));
        Assert.Equal(isProtected, item.IsLocked);
        Assert.Empty(item.LockedFields);
    }

    [Fact]
    public void TheFirstRunsOnPhotosBeforeTheRefreshAndOnALockedItem()
    {
        Assert.IsAssignableFrom<ICustomMetadataProvider<Photo>>(Before());
        Assert.IsAssignableFrom<IPreRefreshProvider>(Before());
        Assert.IsAssignableFrom<IForcedProvider>(Before());
    }

    // PhotoProvider is neither pre-refresh nor ordered, so a forced provider with a later order runs after it.
    [Fact]
    public void ThePhotoProviderOfThePluginRunsAfterTheOneOfJellyfin()
    {
        var provider = AfterPhotoProvider();

        Assert.IsAssignableFrom<ICustomMetadataProvider<Photo>>(provider);
        Assert.IsAssignableFrom<IForcedProvider>(provider);
        Assert.IsNotAssignableFrom<IPreRefreshProvider>(provider);
        Assert.True(provider.Order > 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewPhotoGetsItsSizeAndExifWithoutGpsAndIsLockedOnlyWithProtection(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        var item = Located(await PhotoStubAsync());

        var result = await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport | ItemUpdateType.ImageUpdate, result);
        AssertFromTheRemote(item, isProtected);
        Assert.Equal(Thumbnail(), item.GetImageInfo(ImageType.Primary, 0)?.Path);
    }

    // PhotoProvider points the Primary of an unlocked photo at the 1x1 placeholder: the thumbnail takes it back.
    [Fact]
    public async Task AnUnlockedPhotoGetsItsThumbnailBackAfterPhotoProvider()
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = false;
        var item = Located(await PhotoStubAsync());
        await Before().FetchAsync(item, Options(), CancellationToken.None);
        item.SetImage(new ItemImageInfo { Path = item.Path, Type = ImageType.Primary }, 0);

        var result = await AfterPhotoProvider().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.ImageUpdate, result);
        var primary = Assert.Single(item.GetImages(ImageType.Primary));
        Assert.Equal(Thumbnail(), primary.Path);
        Assert.Equal(System.IO.File.GetLastWriteTimeUtc(Thumbnail()), primary.DateModified);
    }

    [Fact]
    public async Task APhotoWithoutAThumbnailGetsNoPrimary()
    {
        var item = Located(await PhotoStubAsync());
        System.IO.File.Delete(Thumbnail());

        var result = await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.Null(item.GetImageInfo(ImageType.Primary, 0));
    }

    // Without protection PhotoProvider runs and reads the 1x1 placeholder, so the plugin writes the photo again after it.
    [Fact]
    public async Task AnUnlockedPhotoGetsItsDataBackAfterPhotoProvider()
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = false;
        var item = Located(await PhotoStubAsync());
        await Before().FetchAsync(item, Options(), CancellationToken.None);
        (item.Width, item.Height, item.CameraMake, item.Overview, item.Latitude) = (1, 1, null, null, 45);

        var result = await AfterPhotoProvider().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        AssertFromTheRemote(item, false);
    }

    [Fact]
    public async Task AnUnchangedPhotoIsNotSavedAgain()
    {
        var item = Located(await PhotoStubAsync());
        await Before().FetchAsync(item, Options(), CancellationToken.None);

        Assert.Equal(ItemUpdateType.None, await Before().FetchAsync(item, Options(), CancellationToken.None));
        Assert.Equal(ItemUpdateType.None, await AfterPhotoProvider().FetchAsync(item, Options(), CancellationToken.None));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("unknown")]
    [InlineData("no folder")]
    public async Task APhotoThatIsNotAKnownPlaceholderIsLeftAlone(string what)
    {
        var path = await PhotoStubAsync();
        var item = new Photo
        {
            Id = Guid.NewGuid(),
            Name = "Mine",
            Width = 10,
            Latitude = 1,
            Path = what switch
            {
                "outside" => "/media/photos/IMG_0001.jpg",
                "unknown" => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "Other.jpg"),
                _ => path
            }
        };
        if (what == "no folder")
        {
            _fixture.Sync.Store.Configuration.FederatedFolderPath = string.Empty;
        }

        Assert.Equal(ItemUpdateType.None, await Before().FetchAsync(item, Options(), CancellationToken.None));
        Assert.Equal(ItemUpdateType.None, await AfterPhotoProvider().FetchAsync(item, Options(), CancellationToken.None));
        Assert.Equal(("Mine", 10, 1.0, false), (item.Name, item.Width, item.Latitude, item.IsLocked));
        Assert.Empty(item.ProviderIds);
    }

    [Fact]
    public async Task TheSecondLeavesAPhotoAlone()
    {
        var item = Located(await PhotoStubAsync());

        Assert.Equal(ItemUpdateType.None, await After().FetchAsync(item, Options(), CancellationToken.None));
        Assert.Equal(0, item.Width);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnIndexedPhotoGetsItsDataAtTheNextRun(bool isProtected)
    {
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        var path = await PhotoStubAsync();
        var item = Located(path);
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);

        await _fixture.SyncAndMaterializeAsync();

        AssertFromTheRemote(item, isProtected);
    }
}
