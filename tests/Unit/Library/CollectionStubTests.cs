using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class CollectionStubTests : IDisposable
{
    private readonly StubFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private FakeRemoteClient Remote => _fixture.Sync.Client;

    private async Task<(RemoteServerInfo Server, Movie Item)> SyncedFilmInSagaAsync(bool isProtected = true)
    {
        _fixture.Sync.Store.Configuration.FederateCollections = true;
        _fixture.Sync.Store.Configuration.MetadataProtection = isProtected;
        Remote.Add("films", RemoteItemType.Movie, TestItems.Movie("m", "Film", 2020));
        Remote.AddCollection("films", "box", "Saga & Co", "m");
        var server = await _fixture.Sync.AddServerAsync("A", "films");
        await _fixture.SyncAndMaterializeAsync();

        var path = _fixture.Path_(server, "Film", "Film (2020)", "Film (2020) - A.strm");
        var item = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film", CollectionName = "Saga & Co", ShortcutPath = StubFixture.Text(path) };
        _fixture.Library.Setup(library => library.FindByPath(path, false)).Returns(item);
        return (server, item);
    }

    private XElement NfoOf(RemoteServerInfo server)
        => XDocument.Parse(StubFixture.Text(_fixture.Path_(server, "Film", "Film (2020)", "movie.nfo"))).Root!;

    private void Rename(string name, params string[] films)
    {
        Remote.Collections.Clear();
        Remote.AddCollection("films", "box", name, films);
    }

    [Fact]
    public async Task AFilmOfACollectionHasItsSet()
    {
        var (server, _) = await SyncedFilmInSagaAsync();

        Assert.Equal("Saga & Co", NfoOf(server).Element("set")?.Element("name")?.Value);
        Assert.Equal("true", NfoOf(server).Element("lockdata")?.Value);
    }

    [Fact]
    public async Task ARenamedCollectionReachesTheItem()
    {
        var (server, item) = await SyncedFilmInSagaAsync();
        Rename("Saga renamed", "m");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal("Saga renamed", NfoOf(server).Element("set")?.Element("name")?.Value);
        Assert.Equal("Saga renamed", item.CollectionName);
        _fixture.Library.Verify(library => library.UpdateItemAsync(item, It.IsAny<BaseItem>(), ItemUpdateType.MetadataImport, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheTmdbIdOfTheCollectionIsKeptOnTheItem()
    {
        var (_, item) = await SyncedFilmInSagaAsync();
        Remote.Collections.Clear();
        Remote.Collections.Add(("films", new RemoteItem { Id = "box", Name = "Saga", ProviderIds = new Dictionary<string, string?> { ["Tmdb"] = "1241" } }, ["m"]));

        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal("1241", item.ProviderIds["TmdbCollection"]);
        Assert.Equal("1", item.ProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task WithProtectionAFilmThatLeavesItsCollectionLosesIt()
    {
        var (server, item) = await SyncedFilmInSagaAsync();
        Rename("Saga");

        await _fixture.SyncAndMaterializeAsync();

        Assert.Null(NfoOf(server).Element("set"));
        Assert.Null(item.CollectionName);
    }

    [Fact]
    public async Task WithoutProtectionACollectionIsSetButNeverCleared()
    {
        var (_, item) = await SyncedFilmInSagaAsync(isProtected: false);
        Rename("Saga renamed", "m");
        await _fixture.SyncAndMaterializeAsync();
        Assert.Equal("Saga renamed", item.CollectionName);

        Rename("Saga renamed");
        await _fixture.SyncAndMaterializeAsync();

        Assert.Equal("Saga renamed", item.CollectionName);
    }

    [Fact]
    public async Task TurningTheOptionOffDropsTheSetAndLeavesTheItem()
    {
        var (server, item) = await SyncedFilmInSagaAsync();
        _fixture.Sync.Store.Configuration.FederateCollections = false;

        await _fixture.SyncAndMaterializeAsync();

        Assert.Null(NfoOf(server).Element("set"));
        Assert.Equal("Saga & Co", item.CollectionName);
    }
}
