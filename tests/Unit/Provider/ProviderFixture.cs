using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.JellyMesh.Tests.Provider;

/// <summary>A provider with one peer, one shared library and one library that is not shared.</summary>
internal sealed class ProviderFixture
{
    public static readonly Guid SharedLibrary = Guid.Parse("10000000-0000-0000-0000-000000000000");
    public static readonly Guid PrivateLibrary = Guid.Parse("20000000-0000-0000-0000-000000000000");
    public static readonly Guid SharedItem = Guid.Parse("30000000-0000-0000-0000-000000000000");
    public static readonly Guid PrivateItem = Guid.Parse("40000000-0000-0000-0000-000000000000");
    public static readonly Guid StubItem = Guid.Parse("50000000-0000-0000-0000-000000000000");

    public ProviderFixture()
    {
        Store.Configuration.FederatedFolderPath = "/federata";
        Peer = new User("peer", "auth", "reset");
        Slots = new StreamSlots(Clock, Store);
        Closer = new AbandonedStreamCloser(Slots, Transcodes.Object, Clock, NullLogger<AbandonedStreamCloser>.Instance);

        Authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(() => new AuthorizationInfo { User = Caller });

        Directory = new PeerDirectory(Store, Authorization.Object);
        Catalog = new ProviderCatalog(Library.Object, Dto.Object, Store);

        AddItem(SharedItem, new Movie { Path = "/media/film/a.mkv" }, SharedLibrary);
        AddItem(PrivateItem, new Movie { Path = "/other/film/b.mkv" }, PrivateLibrary);
        AddItem(StubItem, new Movie { Path = "/federata/Film/c/c.strm", ShortcutPath = "http://127.0.0.1:8096/JellyMesh/Play/stub" }, SharedLibrary);
        WithLibrary(SharedLibrary, "/media/film");
        Connect();
    }

    public InMemoryConfigurationStore Store { get; } = new();

    public Mock<ILibraryManager> Library { get; } = new();

    public Mock<IDtoService> Dto { get; } = new();

    public Mock<IAuthorizationContext> Authorization { get; } = new();

    public ManualClock Clock { get; } = new();

    public User Peer { get; }

    /// <summary>The user behind the request, null for an anonymous one.</summary>
    public User? Caller { get; set; }

    public StreamSlots Slots { get; }

    public Mock<ITranscodeManager> Transcodes { get; } = new();

    public AbandonedStreamCloser Closer { get; }

    public PeerDirectory Directory { get; }

    public ProviderCatalog Catalog { get; }

    public PeerCaller PeerCaller => new(Directory.Find(Peer.Id)!, Peer);

    /// <summary>Makes a play session the peer's, as its own <c>PlaybackInfo</c> does, with no slot left behind.</summary>
    public void Own(string playSession, Guid? peer = null)
    {
        var id = peer ?? Peer.Id;
        var reservation = Slots.TryReserve(id, int.MaxValue, null)!;
        Slots.Confirm(id, reservation, playSession);
        Slots.Release(id, playSession);
    }

    public void Connect(int maxStreams = 1)
    {
        Store.Configuration.Peers.Clear();
        var peer = new ProviderPeerConfig { UserId = Peer.Id, MaxStreams = maxStreams };
        peer.SharedLibraryIds.Add(SharedLibrary.ToString("N"));
        Store.Configuration.Peers.Add(peer);
        Caller = Peer;
    }

    /// <summary>Makes a film library with these paths the only one on the server.</summary>
    public void WithLibrary(Guid id, params string[] locations)
    {
        Library.Setup(manager => manager.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Film", ItemId = id.ToString(), CollectionType = CollectionTypeOptions.movies, Locations = locations }
        ]);
    }

    /// <summary>Makes a path a folder Jellyfin has indexed.</summary>
    public Folder WithFolder(string path)
    {
        var folder = new Folder { Id = Guid.NewGuid(), Path = path };
        Library.Setup(manager => manager.FindByPath(path, true)).Returns(folder);
        return folder;
    }

    public void AddItem(Guid id, BaseItem item, Guid library)
    {
        item.Id = id;
        Library.Setup(manager => manager.GetItemById<BaseItem>(id, It.IsAny<User>())).Returns(item);
        Library.Setup(manager => manager.GetCollectionFolders(item)).Returns([new Folder { Id = library }]);
    }

    /// <summary>The descriptor MVC would give for a stock action.</summary>
    public static ControllerActionDescriptor Stock(string controller, string action) => new()
    {
        ControllerName = controller,
        ActionName = action,
        ControllerTypeInfo = typeof(ControllerActionDescriptor).GetTypeInfo()
    };

    public static ControllerActionDescriptor Plugin<T>(string action) => new()
    {
        ControllerName = typeof(T).Name.Replace("Controller", string.Empty, StringComparison.Ordinal),
        ActionName = action,
        ControllerTypeInfo = typeof(T).GetTypeInfo()
    };

    public void WithDtos(params BaseItemDto[] dtos)
    {
        Dto.Setup(service => service.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<DtoOptions>(), It.IsAny<User?>(), It.IsAny<BaseItem?>()))
            .Returns(dtos.ToList());
    }

    public static BaseItemDto DtoWith(string sourceId, params MediaStream[] streams) => new()
    {
        Id = Guid.NewGuid(),
        MediaSources = [new MediaSourceInfo { Id = sourceId, Path = "/media/secret.mkv", MediaStreams = streams.ToList() }]
    };
}
