using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

public class ProviderVersionTests
{
    private static readonly Guid _other = Guid.Parse("60000000-0000-0000-0000-000000000000");

    private readonly ProviderFixture _fixture = new();

    private static MediaSourceInfo Source(Guid id) => Source(id.ToString("N"));

    private static MediaSourceInfo Source(string? id) => new() { Id = id, Path = "/media/any.mkv", MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Height = 1080 }] };

    // What a peer is sent of the shared film with these versions.
    private ProviderItemsResponse Sent(params MediaSourceInfo[] sources)
    {
        _fixture.WithDtos(new BaseItemDto { Id = ProviderFixture.SharedItem, MediaSources = sources });
        return _fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie);
    }

    private static string[] Ids(ProviderItemsResponse response) => response.Items[0].MediaSources.Select(source => source.Id).ToArray();

    // A file next to the main file of a film, which is no item of a library by itself.
    private void AddFileVersion(Guid owner, string path = "/media/film/a - 720p.mkv", string? shortcut = null)
    {
        var file = new Movie { Id = _other, Path = path, ShortcutPath = shortcut, OwnerId = owner };
        _fixture.Library.Setup(manager => manager.GetItemById<BaseItem>(_other, It.IsAny<User>())).Returns(file);
        _fixture.Library.Setup(manager => manager.GetCollectionFolders(file)).Returns([]);
    }

    [Fact]
    public void A_peer_is_sent_the_version_of_the_item_itself()
    {
        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], Ids(Sent(Source(ProviderFixture.SharedItem))));
    }

    [Fact]
    public void A_peer_is_sent_a_version_in_a_library_shared_with_it()
    {
        _fixture.AddItem(_other, new Movie { Path = "/media/film/a-2160p.mkv" }, ProviderFixture.SharedLibrary);

        Assert.Equal([ProviderFixture.SharedItem.ToString("N"), _other.ToString("N")], Ids(Sent(Source(ProviderFixture.SharedItem), Source(_other))));
    }

    [Fact]
    public void A_peer_is_sent_a_file_version_of_an_item_shared_with_it()
    {
        AddFileVersion(ProviderFixture.SharedItem);

        Assert.Equal([ProviderFixture.SharedItem.ToString("N"), _other.ToString("N")], Ids(Sent(Source(ProviderFixture.SharedItem), Source(_other))));
    }

    [Fact]
    public void A_peer_is_not_sent_a_file_version_of_an_item_not_shared_with_it()
    {
        AddFileVersion(ProviderFixture.PrivateItem);

        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], Ids(Sent(Source(ProviderFixture.SharedItem), Source(_other))));
    }

    [Fact]
    public void A_peer_is_not_sent_a_version_in_no_library()
    {
        AddFileVersion(Guid.Empty);

        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], Ids(Sent(Source(ProviderFixture.SharedItem), Source(_other))));
    }

    [Theory]
    [InlineData("/federata/Film/c/c.strm", "http://127.0.0.1:8096/JellyMesh/Play/stub")]
    [InlineData("/media/old/c.strm", "http://127.0.0.1:8096/JellyMesh/Play/gone?sig=00ff")]
    [InlineData("/federata/Film/c/c.strm", null)]
    public void A_peer_is_not_sent_a_federated_version(string path, string? shortcut)
    {
        _fixture.AddItem(_other, new Movie { Path = path, ShortcutPath = shortcut }, ProviderFixture.SharedLibrary);

        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], Ids(Sent(Source(_other), Source(ProviderFixture.SharedItem))));
    }

    [Fact]
    public void A_peer_is_not_sent_a_federated_version_that_has_an_owner_shared_with_it()
    {
        AddFileVersion(ProviderFixture.SharedItem, "/media/film/a.strm", "http://127.0.0.1:8096/JellyMesh/Play/stub");

        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], Ids(Sent(Source(ProviderFixture.SharedItem), Source(_other))));
    }

    [Fact]
    public void A_peer_is_not_sent_a_version_in_a_library_not_shared_with_it()
    {
        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], Ids(Sent(Source(ProviderFixture.PrivateItem), Source(ProviderFixture.SharedItem))));
    }

    [Theory]
    [InlineData("70000000000000000000000000000000")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-an-id")]
    [InlineData("")]
    [InlineData(null)]
    public void A_peer_is_not_sent_a_version_that_cannot_be_found(string? id)
    {
        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], Ids(Sent(Source(id), Source(ProviderFixture.SharedItem))));
    }

    [Fact]
    public void The_quality_and_the_total_follow_the_titles_sent()
    {
        var sent = Sent(Source(ProviderFixture.StubItem), Source(ProviderFixture.PrivateItem), Source(ProviderFixture.SharedItem));

        Assert.Equal([ProviderFixture.SharedItem.ToString("N")], sent.Quality.Keys);
        Assert.Equal(1, sent.TotalRecordCount);
        Assert.Single(sent.Items);
    }

    [Fact]
    public void An_item_without_versions_is_sent_as_it_is()
    {
        _fixture.WithDtos(new BaseItemDto { Id = ProviderFixture.SharedItem });

        Assert.Null(_fixture.Catalog.ReadItems(_fixture.PeerCaller, [ProviderFixture.SharedItem], RemoteItemType.Movie).Items[0].MediaSources);
    }
}
