using System;
using System.IO;
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

    // In "none" mode the admin may add the server folder through an alias: the real path is what tells a stub.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATrackReachedThroughALinkIsStillAStub(bool linkedRoot)
    {
        var temp = Directory.CreateTempSubdirectory("jellymesh-real-").FullName;
        try
        {
            var real = Path.Combine(temp, "federata");
            var album = Path.Combine(real, "S", "L", "Musica", "A", "B");
            Directory.CreateDirectory(album);
            File.WriteAllText(Path.Combine(album, "01 - X.strm"), "http://127.0.0.1/strm");
            var alias = Path.Combine(temp, "alias");
            Directory.CreateSymbolicLink(alias, real);
            var radio = Path.Combine(temp, "radio");
            Directory.CreateDirectory(radio);
            File.WriteAllText(Path.Combine(radio, "station.strm"), "http://radio.example/stream");

            _store.Configuration.FederatedFolderPath = linkedRoot ? alias : real;
            var seen = Path.Combine(linkedRoot ? real : alias, "S", "L", "Musica", "A", "B", "01 - X.strm");
            var stub = new StubEntry { Id = "track", Kind = StubKind.Track, IdentityKey = "Track|item:s:t", Sources = [], RelativePath = "S/L/Musica/A/B/01 - X.strm" };
            var track = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Path = seen };
            var station = new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid(), Path = Path.Combine(radio, "station.strm") };
            _library.Setup(library => library.GetItemById(track.Id)).Returns(track);
            _library.Setup(library => library.GetItemById(station.Id)).Returns(station);
            var underRoot = Path.Combine(_store.Configuration.FederatedFolderPath, "S", "L", "Musica", "A", "B", "01 - X.strm");
            _stubs.Setup(stubs => stubs.FindByPath(underRoot)).Returns(stub);

            Assert.True(Lookup().IsAudioStub(track.Id));
            Assert.True(Lookup().IsStub(track.Id));
            Assert.Same(stub, Lookup().FindFile(track.Id));
            Assert.False(Lookup().IsAudioStub(station.Id));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    // A book has no URL either, so the real path is what tells a placeholder reached through an alias, as for a track.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABookReachedThroughALinkIsStillAPlaceholder(bool linkedRoot)
    {
        var temp = Directory.CreateTempSubdirectory("jellymesh-real-").FullName;
        try
        {
            var real = Path.Combine(temp, "federata");
            var folder = Path.Combine(real, "S", "L", "Libri", "Dune (1965)");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Dune (1965).epub"), "placeholder");
            var alias = Path.Combine(temp, "alias");
            Directory.CreateSymbolicLink(alias, real);
            var shelf = Path.Combine(temp, "shelf");
            Directory.CreateDirectory(shelf);
            File.WriteAllText(Path.Combine(shelf, "Dune.epub"), "mine");

            _store.Configuration.FederatedFolderPath = linkedRoot ? alias : real;
            var seen = Path.Combine(linkedRoot ? real : alias, "S", "L", "Libri", "Dune (1965)", "Dune (1965).epub");
            var stub = new StubEntry { Id = "book", Kind = StubKind.Book, IdentityKey = "Book|item:s:b", Sources = [], RelativePath = "S/L/Libri/Dune (1965)/Dune (1965).epub" };
            var book = new Book { Id = Guid.NewGuid(), Path = seen };
            var local = new Book { Id = Guid.NewGuid(), Path = Path.Combine(shelf, "Dune.epub") };
            _library.Setup(library => library.GetItemById(book.Id)).Returns(book);
            _library.Setup(library => library.GetItemById(local.Id)).Returns(local);
            var underRoot = Path.Combine(_store.Configuration.FederatedFolderPath, "S", "L", "Libri", "Dune (1965)", "Dune (1965).epub");
            _stubs.Setup(stubs => stubs.FindByPath(underRoot)).Returns(stub);

            Assert.True(Lookup().IsPlaceholder(book.Id));
            Assert.True(Lookup().IsStub(book.Id));
            Assert.Same(stub, Lookup().FindFile(book.Id));
            Assert.Same(stub, Lookup().Find(book.Id));
            Assert.False(Lookup().IsPlaceholder(local.Id));
            Assert.Null(Lookup().FindFile(local.Id));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }
}
