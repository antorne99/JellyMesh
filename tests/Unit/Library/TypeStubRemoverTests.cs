using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class TypeStubRemoverTests : IDisposable
{
    private static readonly Guid _server = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellymesh-type-remover-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ILibraryManager> _library = new();
    private readonly StubRemover _remover;

    public TypeStubRemoverTests()
    {
        _remover = new StubRemover(_library.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static string HomeFolder(params string[] parts)
        => Path.Combine(FederatedPaths.TypeFolderOf(_server, TestLibraries.HomeVideos, TypeFolder.HomeVideos), Path.Combine(parts));

    private StubEntry Stub(string id, StubKind kind, string relative, params string[] siblings)
    {
        var strm = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(strm)!);
        File.WriteAllText(strm, "url");
        foreach (var sibling in siblings)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(strm)!, sibling), "x");
        }

        return new StubEntry { Id = id, ServerId = _server, Kind = kind, RelativePath = relative, Sources = [], IdentityKey = id };
    }

    private string Abs(string relative) => Path.Combine(_root, relative);

    [Fact]
    public void AHomeVideoTakesOnlyItsOwnFiles()
    {
        var gone = Stub("v1", StubKind.HomeVideo, HomeFolder("Trips", "clip.strm"), "clip.nfo", "clip-thumb.jpg");
        var other = Stub("v2", StubKind.HomeVideo, HomeFolder("Trips", "clip.final.strm"), "clip.final.nfo", "clip-thumb.final.strm");
        var state = new StubState { Stubs = [gone, other] };

        var removal = _remover.RemoveAll(_root, state, [gone]);

        Assert.Equal(other, Assert.Single(removal.State.Stubs));
        Assert.Equal(
            ["clip-thumb.final.strm", "clip.final.nfo", "clip.final.strm"],
            Directory.GetFiles(Abs(HomeFolder("Trips"))).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AHomeVideoNamedLikeTheThumbnailOfAnotherStays()
    {
        var gone = Stub("v1", StubKind.HomeVideo, HomeFolder("Trips", "clip.strm"), "clip-thumb.jpg");
        var other = Stub("v2", StubKind.HomeVideo, HomeFolder("Trips", "clip-thumb.strm"), "clip-thumb.nfo");

        _remover.RemoveAll(_root, new StubState { Stubs = [gone, other] }, [gone]);

        Assert.Equal(
            ["clip-thumb.nfo", "clip-thumb.strm"],
            Directory.GetFiles(Abs(HomeFolder("Trips"))).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheLastHomeVideoOfAFolderTakesTheFoldersItLeavesEmptyButNotTheTypeFolder()
    {
        var gone = Stub("v1", StubKind.HomeVideo, HomeFolder("Trips", "Sea", "Beach day.strm"), "Beach day.nfo");
        var kept = Stub("v2", StubKind.HomeVideo, HomeFolder("Other.strm"));
        var sea = Abs(HomeFolder("Trips", "Sea"));
        var row = new Folder { Id = Guid.NewGuid(), Path = sea };
        _library.Setup(library => library.FindByPath(sea, true)).Returns(row);

        var removal = _remover.RemoveAll(_root, new StubState { Stubs = [gone, kept] }, [gone]);

        Assert.Empty(removal.Failures);
        Assert.False(Directory.Exists(Abs(HomeFolder("Trips"))));
        Assert.True(File.Exists(Abs(kept.RelativePath)));
        _library.Verify(library => library.DeleteItem(row, It.Is<DeleteOptions>(options => !options.DeleteFileLocation)), Times.Once);
    }

    [Fact]
    public void AFolderWithAnotherHomeVideoInASubfolderStays()
    {
        var gone = Stub("v1", StubKind.HomeVideo, HomeFolder("Trips", "Beach day.strm"));
        var kept = Stub("v2", StubKind.HomeVideo, HomeFolder("Trips", "Sea", "Waves.strm"));

        _remover.RemoveAll(_root, new StubState { Stubs = [gone, kept] }, [gone]);

        Assert.False(File.Exists(Abs(gone.RelativePath)));
        Assert.True(File.Exists(Abs(kept.RelativePath)));
    }

    [Fact]
    public void AMusicVideoTakesItsTitleFolder()
    {
        var folder = Path.Combine(FederatedPaths.TypeFolderOf(_server, TestLibraries.MusicVideos, TypeFolder.MusicVideos), "Song (2019)");
        var gone = Stub("m1", StubKind.MusicVideo, Path.Combine(folder, "Song (2019) - Casa.strm"), "Song (2019) - Casa.nfo", "poster.jpg");

        var removal = _remover.RemoveAll(_root, new StubState { Stubs = [gone] }, [gone]);

        Assert.Empty(removal.State.Stubs);
        Assert.False(Directory.Exists(Abs(folder)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(Abs(folder))));
    }
}
