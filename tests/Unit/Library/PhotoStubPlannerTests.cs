using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class PhotoStubPlannerTests
{
    private const string HomeVideos = TestLibraries.HomeVideos;

    private static readonly Guid _serverA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);

    private static readonly IndexedPhoto _exif = new() { Width = 4000, Height = 3000, CameraMake = "Canon", CameraModel = "R6", IsoSpeedRating = 100 };

    private static IndexedItem Photo(string id, string name = "IMG_0001", string parentId = "a1", string? extension = "jpg") => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Photo,
        LibraryId = HomeVideos,
        LastSeenUtc = _now,
        Name = name,
        ParentId = parentId,
        Overview = "Sea",
        PremiereDate = new DateTimeOffset(2021, 7, 8, 0, 0, 0, TimeSpan.Zero),
        ImageTags = new Dictionary<string, string> { ["Primary"] = "photo1" },
        Extension = extension,
        Photo = _exif
    };

    private static IndexedItem Album(string id = "a1", string name = "Trips", string? cover = "cover1") => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.PhotoAlbum,
        LibraryId = HomeVideos,
        LastSeenUtc = _now,
        Name = name,
        ParentId = HomeVideos,
        ImageTags = cover is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Primary"] = cover }
    };

    private static IndexedItem HomeVideo(string id, string name, string parentId = "a1") => new()
    {
        ItemId = id,
        Kind = IndexedItemKind.Video,
        LibraryId = HomeVideos,
        LastSeenUtc = _now,
        Name = name,
        ParentId = parentId,
        Versions = [new IndexedVersion { MediaSourceId = id, Height = 1080, Codec = "h264", BitRate = 1000, VideoRange = "SDR" }]
    };

    private static StubPlan Plan(StubState? state = null, bool isProtected = true, params IndexedItem[] items)
    {
        var snapshot = new IndexSnapshot
        {
            Version = IndexSnapshot.CurrentVersion,
            Health = IndexHealth.Ok,
            Servers = [new ServerCatalog { ServerId = _serverA, State = ServerSyncState.Complete, Items = items }]
        };
        return StubPlanner.PlanByServer(new StubPlanInput(
            new FederationView(IndexHealth.Ok, 0, [], []),
            snapshot,
            state ?? StubState.Empty,
            [new RemoteServerInfo(_serverA, "Casa", "http://server", RemoteMode.Standard, 0, [HomeVideos], null, null)],
            isProtected,
            id => "http://127.0.0.1:8096/JellyMesh/Play/" + id,
            _ => null));
    }

    private static string In(params string[] parts) => Path.Combine(FederatedPaths.TypeFolderOf(_serverA, HomeVideos, TypeFolder.HomeVideos), Path.Combine(parts));

    private static List<StubWork> Photos(StubPlan plan) => plan.Stubs.Where(work => work.Entry.Kind == StubKind.Photo).ToList();

    private static string NameOf(StubWork work) => Path.GetFileNameWithoutExtension(work.Entry.RelativePath);

    [Fact]
    public void APhotoGetsAPlaceholderWithItsExtensionInTheFolderOfItsAlbum()
    {
        var plan = Plan(items: [Album(), Photo("p1")]);

        var work = Assert.Single(plan.Stubs);
        Assert.Equal(StubKind.Photo, work.Entry.Kind);
        Assert.Equal(In("Trips", "IMG_0001.jpg"), work.Entry.RelativePath);
        Assert.Equal(_serverA, work.Entry.ServerId);
        var source = Assert.Single(work.Entry.Sources);
        Assert.Equal((_serverA, "p1", "p1", HomeVideos), (source.ServerId, source.ItemId, source.MediaSourceId, source.LibraryId));
        Assert.Empty(work.Nfo);
        Assert.Empty(work.NfoPath);
        Assert.Empty(work.StrmUrl);
        Assert.Equal("IMG_0001", work.Metadata.Title);
        Assert.Equal(_exif, work.Metadata.Photo);
        Assert.Equal(work.Entry.Id, work.Metadata.ProviderIds[FederatedPlaceholderMark.ProviderId]);
        Assert.Contains(plan.State.Stubs, stub => stub.Id == work.Entry.Id);
    }

    [Fact]
    public void OnePhotoOfAnAlbumCarriesItsCoverAsTheFolderImage()
    {
        var works = Photos(Plan(items: [Album(), Photo("p1"), Photo("p2", "IMG_0002")]));

        var image = Assert.Single(works.SelectMany(work => work.Images));
        Assert.Equal(new StubImage(ImageType.Primary, In("Trips", "folder"), _serverA, "a1", null), image);
        Assert.Single(works, work => work.ImageTag.Length > 0);
    }

    [Fact]
    public void APhotoOfAnAlbumWithNoCoverHasNoImage()
    {
        Assert.Empty(Assert.Single(Plan(items: [Album(cover: null), Photo("p1")]).Stubs).Images);
    }

    [Fact]
    public void APhotoAtTheTopOfItsLibraryHasNoFolderAndNoCover()
    {
        var work = Assert.Single(Plan(items: Photo("p1", parentId: HomeVideos)).Stubs);

        Assert.Equal(In("IMG_0001.jpg"), work.Entry.RelativePath);
        Assert.Empty(work.Images);
    }

    [Theory]
    [InlineData("Trip", "Trip-1")]
    [InlineData("Trip", "trip 2")]
    [InlineData("Trip", "Trip")]
    public void APhotoWhoseNameStartsWithAHomeVideoOfItsFolderGetsANameThatDoesNot(string video, string photo)
    {
        var plan = Plan(items: [Album(), HomeVideo("a0", video), Photo("p1", photo)]);

        var name = NameOf(Assert.Single(Photos(plan)));
        Assert.False(name.StartsWith(video, StringComparison.OrdinalIgnoreCase), name);
        Assert.Contains(photo, name, StringComparison.Ordinal);
    }

    [Fact]
    public void AHomeVideoWhoseNameStartsAPhotoOfItsFolderGetsANameThatDoesNot()
    {
        var plan = Plan(items: [Album(), Photo("p1", "Trip-1"), HomeVideo("v1", "Trip")]);

        var video = Path.GetFileNameWithoutExtension(Assert.Single(plan.Stubs, work => work.Entry.Kind == StubKind.HomeVideo).Entry.RelativePath);
        Assert.Equal("Trip-1", NameOf(Assert.Single(Photos(plan))));
        Assert.False("Trip-1".StartsWith(video, StringComparison.OrdinalIgnoreCase), video);
    }

    [Fact]
    public void APhotoAndAHomeVideoOfDifferentFoldersKeepTheirNames()
    {
        var plan = Plan(items: [Album(), Album("a2", "Other"), HomeVideo("v1", "Trip", "a2"), Photo("p1", "Trip-1")]);

        Assert.Equal("Trip-1", NameOf(Assert.Single(Photos(plan))));
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("Cover of the book")]
    [InlineData("thumbnail")]
    [InlineData("POSTER")]
    [InlineData("backdrop 1")]
    [InlineData("fanart")]
    [InlineData("landscape")]
    [InlineData("logo")]
    [InlineData("Default")]
    public void APhotoNamedLikeArtworkGetsANameJellyfinReadsAsAPhoto(string photo)
    {
        var name = NameOf(Assert.Single(Plan(items: [Album(), Photo("p1", photo)]).Stubs));

        Assert.DoesNotContain(new[] { "folder", "thumb", "landscape", "fanart", "backdrop", "poster", "cover", "logo", "default" }, word => name.StartsWith(word, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(photo, name, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPhotosWithOneFileNameInAFolderGetTwoFiles()
    {
        var works = Photos(Plan(items: [Album(), Photo("p1"), Photo("p2", "img_0001")]));

        Assert.Equal(2, works.Select(work => work.Entry.RelativePath.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void TwoPhotosWithOneNameAndTwoFormatsKeepTheirNames()
    {
        var works = Photos(Plan(items: [Album(), Photo("p1"), Photo("p2", extension: "png")]));

        Assert.Equal(["IMG_0001.jpg", "IMG_0001.png"], works.Select(work => Path.GetFileName(work.Entry.RelativePath)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void APhotoWithNoKnownFormatIsLeftOut()
    {
        Assert.Empty(Plan(items: [Album(), Photo("p1", extension: null)]).Stubs);
    }

    [Fact]
    public void AnExistingPhotoStubKeepsItsIdAndPathAndOneGoneIsRemoved()
    {
        var first = Plan(items: [Album(), Photo("p1"), Photo("p2", "IMG_0002")]);
        var kept = Assert.Single(Photos(first), work => work.Entry.Sources[0].ItemId == "p1").Entry;

        var second = Plan(first.State, items: [Album(), Photo("p1", "Renamed")]);

        var work = Assert.Single(second.Stubs);
        Assert.Equal((kept.Id, kept.RelativePath), (work.Entry.Id, work.Entry.RelativePath));
        Assert.False(work.IsNew);
        Assert.Equal("p2", Assert.Single(second.Removals).Sources[0].ItemId);
    }

    [Fact]
    public void APhotoInAnotherFormatNowIsANewStub()
    {
        var first = Plan(items: [Album(), Photo("p1")]);

        var second = Plan(first.State, items: [Album(), Photo("p1", extension: "png")]);

        Assert.EndsWith(".png", Assert.Single(second.Stubs).Entry.RelativePath, StringComparison.Ordinal);
        Assert.Equal(Assert.Single(first.Stubs).Entry.Id, Assert.Single(second.Removals).Id);
    }

    [Fact]
    public void AnUnprotectedPhotoKeepsItsSizeAndCameraUnlocked()
    {
        var metadata = Assert.Single(Plan(isProtected: false, items: [Album(), Photo("p1")]).Stubs).Metadata;

        Assert.False(metadata.IsProtected);
        Assert.Equal(_exif, metadata.Photo);
        Assert.Equal("Sea", metadata.Overview);
    }

    [Fact]
    public void TheHashFollowsTheMetadata()
    {
        var first = Plan(items: [Album(), Photo("p1")]);
        var same = Assert.Single(Plan(first.State, items: [Album(), Photo("p1")]).Stubs).ContentHash;
        var changed = Assert.Single(Plan(first.State, items: [Album(), Photo("p1") with { Photo = _exif with { CameraModel = "R5" } }]).Stubs).ContentHash;

        Assert.Equal(Assert.Single(first.Stubs).ContentHash, same);
        Assert.NotEqual(same, changed);
    }

    // A photo edited on the remote gets a new Primary tag there, so its thumbnail is read again.
    [Fact]
    public void TheHashFollowsThePhotoImage()
    {
        var first = Plan(items: [Album(), Photo("p1")]);
        var edited = Photo("p1") with { ImageTags = new Dictionary<string, string> { ["Primary"] = "edited" } };

        var changed = Assert.Single(Plan(first.State, items: [Album(), edited]).Stubs).ContentHash;

        Assert.NotEqual(Assert.Single(first.Stubs).ContentHash, changed);
    }
}
