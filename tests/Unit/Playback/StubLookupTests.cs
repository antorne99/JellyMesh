using System;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

/// <summary>How <see cref="StubLookup"/> tells stubs while the federated folder is not known.</summary>
public class StubLookupTests
{
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IStubRegistry> _stubs = new();
    private readonly InMemoryConfigurationStore _store = new();

    public StubLookupTests() => _store.Configuration.FederatedFolderPath = "/federata";

    private StubLookup Lookup() => new(_library.Object, _stubs.Object, _store);

    private Movie Item(string path)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Path = path };
        _library.Setup(library => library.GetItemById(movie.Id)).Returns(movie);
        return movie;
    }

    // Stock would send the text of a .strm it has not read, with its signed URL, so each one counts as a stub.
    [Fact]
    public void WhileTheSettingsCannotBeReadEveryStrmIsAStubAndNothingElse()
    {
        var strm = Item("/federata/S/Film/A/A.strm");
        var local = Item("/media/film/A.mkv");
        _store.Unavailable = true;

        Assert.True(Lookup().IsStub(strm.Id));
        Assert.False(Lookup().IsStub(local.Id));
    }

    [Fact]
    public void WhileTheSettingsCannotBeReadAFilmLinkedToAStrmHasVersionsAndTwoLocalOnesStayStock()
    {
        var strm = Item("/federata/S/Film/A/A.strm");
        var local = Item("/media/film/A.mkv");
        local.LinkedAlternateVersions = [new LinkedChild { ItemId = strm.Id }];
        var alone = Item("/media/film/B.mkv");
        var other = Item("/media/film/B 4K.mkv");
        alone.LinkedAlternateVersions = [new LinkedChild { ItemId = other.Id }];
        _store.Unavailable = true;

        Assert.True(Lookup().HasVersions(local.Id));
        Assert.False(Lookup().HasVersions(alone.Id));
    }

    // A track stub has no plugin URL: the file routes find it by its path, playback does not until F56.
    [Fact]
    public void ATrackStrmInTheFolderIsAStubFoundForItsFileOnly()
    {
        var stub = new StubEntry { Id = "track", Kind = StubKind.Track, IdentityKey = "Track|item:s:t", Sources = [], RelativePath = "S/L/Musica/A/B/01 - X.strm" };
        var track = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Path = "/federata/S/L/Musica/A/B/01 - X.strm" };
        var local = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Path = "/media/music/01 - X.strm" };
        _library.Setup(library => library.GetItemById(track.Id)).Returns(track);
        _library.Setup(library => library.GetItemById(local.Id)).Returns(local);
        _stubs.Setup(stubs => stubs.FindByPath(track.Path)).Returns(stub);

        Assert.True(Lookup().IsStub(track.Id));
        Assert.True(Lookup().IsAudioStub(track.Id));
        Assert.Same(stub, Lookup().FindFile(track.Id));
        Assert.Null(Lookup().Find(track.Id));
        Assert.False(Lookup().IsStub(local.Id));
        Assert.Null(Lookup().FindFile(local.Id));
    }
}
