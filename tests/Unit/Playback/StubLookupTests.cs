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
}
