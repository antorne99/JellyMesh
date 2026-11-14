using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Tests.Sync;

/// <summary>An in-memory remote: libraries with items per type, and hooks to make it misbehave.</summary>
internal sealed class FakeRemoteClient : IJellyfinRemoteClient
{
    public List<RemoteLibrary> Libraries { get; } =
    [
        new("films", "Film", "movies"),
        new("shows", "Serie", "tvshows")
    ];

    /// <summary>The items by library and type.</summary>
    public Dictionary<(string Library, RemoteItemType Type), List<RemoteItem>> Items { get; } = [];

    /// <summary>Items only found by ID, such as the rows of a series that <c>/Items</c> collapses.</summary>
    public List<RemoteItem> ItemsById { get; } = [];

    /// <summary>A hidden row and the row a lookup of its ID answers with, as Jellyfin does for a split series.</summary>
    public Dictionary<string, string> RepresentedBy { get; } = [];

    public List<RemoteItemQuery> Queries { get; } = [];

    public int Logins { get; private set; }

    /// <summary>The clock of the remote. Null answers without a Date header.</summary>
    public DateTimeOffset? ServerTime { get; set; } = SyncFixture.Start;

    public int ClockReads { get; private set; }

    /// <summary>Fails the clock read.</summary>
    public RemoteFailure ClockFailure { get; set; }

    /// <summary>When each item was last saved, by ID. An item not listed has never been saved since the start.</summary>
    public Dictionary<string, DateTimeOffset> SavedUtc { get; } = [];

    /// <summary>Runs before each page. Returning a failure fails the page.</summary>
    public Func<RemoteItemQuery, int, RemoteFailure> OnPage { get; set; } = (_, _) => RemoteFailure.None;

    /// <summary>Rewrites a page before it is returned.</summary>
    public Func<RemoteItemQuery, RemoteItemPage, RemoteItemPage> Rewrite { get; set; } = (_, page) => page;

    public void Add(string library, RemoteItemType type, params RemoteItem[] items)
    {
        if (!Items.TryGetValue((library, type), out var list))
        {
            list = [];
            Items[(library, type)] = list;
        }

        list.AddRange(items);
    }

    /// <summary>The Server Id the remote gives itself at login. Null gives none, as a server before this was read.</summary>
    public string? ServerId { get; set; }

    /// <summary>The device of each login, in order.</summary>
    public List<string> LoginDevices { get; } = [];

    /// <summary>The device of each session ended.</summary>
    public List<string> LogoutDevices { get; } = [];

    public Task<RemoteResult<RemoteSession>> AuthenticateAsync(RemoteEndpoint endpoint, RemoteCredentials credentials, CancellationToken cancellationToken)
    {
        Logins++;
        LoginDevices.Add(endpoint.DeviceId);
        return Task.FromResult(RemoteResult.Success(new RemoteSession(TestValues.Token, "abc") { ServerId = ServerId }));
    }

    public Task<RemoteResult<bool>> LogoutAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        LogoutDevices.Add(endpoint.DeviceId);
        return Task.FromResult(RemoteResult.Success(true));
    }

    /// <summary>What the capability endpoint answers.</summary>
    public RemoteCapabilities Capabilities { get; set; } = new(RemoteMode.Standard, 0);

    /// <summary>Fails the capability detection.</summary>
    public RemoteFailure CapabilityFailure { get; set; }

    /// <summary>The libraries a JellyMesh remote shares. Null lists <see cref="Libraries"/>.</summary>
    public List<RemoteLibrary>? SharedLibraries { get; set; }

    public int SharedLibraryReads { get; private set; }

    /// <summary>The cursors the feed was read with.</summary>
    public List<string?> FeedReads { get; } = [];

    /// <summary>Answers the feed. Without a cursor the default says where it stands.</summary>
    public Func<string?, RemoteResult<RemoteChangePage>> Feed { get; set; } =
        cursor => RemoteResult.Success(new RemoteChangePage(cursor is null, "cursor-0", false, []));

    public Task<RemoteResult<RemoteCapabilities>> DetectCapabilitiesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
        => Task.FromResult(CapabilityFailure == RemoteFailure.None ? RemoteResult.Success(Capabilities) : RemoteResult.Fail<RemoteCapabilities>(CapabilityFailure));

    public Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
        => Task.FromResult(RemoteResult.Success<IReadOnlyList<RemoteLibrary>>(Libraries.ToList()));

    public Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetSharedLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        SharedLibraryReads++;
        return Task.FromResult(RemoteResult.Success<IReadOnlyList<RemoteLibrary>>((SharedLibraries ?? Libraries).ToList()));
    }

    public Task<RemoteResult<RemoteChangePage>> GetChangesAsync(RemoteEndpoint endpoint, RemoteSession session, string? cursor, CancellationToken cancellationToken)
    {
        FeedReads.Add(cursor);
        return Task.FromResult(Feed(cursor));
    }

    /// <summary>Runs before each read of the clock, to make the remote slow.</summary>
    public Func<int, CancellationToken, Task> BeforeClock { get; set; } = (_, _) => Task.CompletedTask;

    public async Task<RemoteResult<DateTimeOffset?>> GetServerTimeAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ClockReads++;
        await BeforeClock(ClockReads, cancellationToken);
        return ClockFailure == RemoteFailure.None ? RemoteResult.Success(ServerTime) : RemoteResult.Fail<DateTimeOffset?>(ClockFailure);
    }

    /// <summary>The images requested, as item/type/index.</summary>
    public List<string> ImageReads { get; } = [];

    /// <summary>Fails the image reads.</summary>
    public RemoteFailure ImageFailure { get; set; }

    /// <summary>Runs inside every image read, to hold it open.</summary>
    public Func<CancellationToken, Task> BeforeImage { get; set; } = _ => Task.CompletedTask;

    /// <summary>The most image reads that were open at once.</summary>
    public int MaxImagesInFlight { get; private set; }

    private int _imagesInFlight;

    public async Task<RemoteResult<RemoteImage>> GetImageAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, string imageType, int imageIndex, CancellationToken cancellationToken)
    {
        lock (ImageReads)
        {
            ImageReads.Add(itemId + "/" + imageType + "/" + imageIndex);
            MaxImagesInFlight = Math.Max(MaxImagesInFlight, ++_imagesInFlight);
        }

        try
        {
            await BeforeImage(cancellationToken);
            return ImageFailure == RemoteFailure.None
                ? RemoteResult.Success(new RemoteImage(new byte[] { 0xFF, 0xD8, 0xFF }, "image/jpeg"))
                : RemoteResult.Fail<RemoteImage>(ImageFailure);
        }
        finally
        {
            lock (ImageReads)
            {
                _imagesInFlight--;
            }
        }
    }

    public Task<RemoteResult<RemoteItemPage>> GetItemsAsync(RemoteEndpoint endpoint, RemoteSession session, RemoteItemQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        var failure = OnPage(query, Queries.Count);
        if (failure != RemoteFailure.None)
        {
            return Task.FromResult(RemoteResult.Fail<RemoteItemPage>(failure));
        }

        if (query.Ids is { } ids)
        {
            var wanted = ids.Select(id => RepresentedBy.GetValueOrDefault(id, id)).ToList();
            var found = ItemsById.Where(item => wanted.Contains(item.Id)).ToList();
            return Task.FromResult(RemoteResult.Success(new RemoteItemPage(found, found.Count)));
        }

        var all = Items.GetValueOrDefault((query.LibraryId!, query.Type)) ?? [];
        if (query.ChangedSinceUtc is { } since)
        {
            all = all.Where(item => SavedUtc.TryGetValue(item.Id, out var saved) && saved >= since).ToList();
        }

        var page = new RemoteItemPage(all.Skip(query.StartIndex).Take(query.Limit).ToList(), all.Count);
        return Task.FromResult(RemoteResult.Success(Rewrite(query, page)));
    }

    /// <summary>The collections, each with its library and the IDs of its films.</summary>
    public List<(string Library, RemoteItem Collection, List<string> Films)> Collections { get; } = [];

    /// <summary>The collection reads, as library or collection ID and start index.</summary>
    public List<string> CollectionReads { get; } = [];

    /// <summary>Fails the collection reads.</summary>
    public RemoteFailure CollectionFailure { get; set; }

    public void AddCollection(string library, string id, string name, params string[] films)
        => Collections.Add((library, new RemoteItem { Id = id, Name = name }, films.ToList()));

    public Task<RemoteResult<RemoteItemPage>> GetCollectionsAsync(RemoteEndpoint endpoint, RemoteSession session, string libraryId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        CollectionReads.Add(libraryId + "@" + startIndex);
        var all = Collections.Where(entry => entry.Library == libraryId).Select(entry => entry.Collection).ToList();
        return Task.FromResult(CollectionFailure == RemoteFailure.None
            ? RemoteResult.Success(new RemoteItemPage(all.Skip(startIndex).Take(limit).ToList(), all.Count))
            : RemoteResult.Fail<RemoteItemPage>(CollectionFailure));
    }

    /// <summary>What the ProviderApi of a JellyMesh remote answers on collections, by library.</summary>
    public List<(string Library, RemoteCollectionEntry Entry)> SharedCollections { get; } = [];

    /// <summary>Rewrites a page of the ProviderApi on collections before it is returned.</summary>
    public Func<RemoteCollectionPage, RemoteCollectionPage> RewriteSharedCollections { get; set; } = page => page;

    public Task<RemoteResult<RemoteCollectionPage>> GetSharedCollectionsAsync(RemoteEndpoint endpoint, RemoteSession session, string libraryId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        CollectionReads.Add("peer:" + libraryId + "@" + startIndex);
        var all = SharedCollections.Where(entry => entry.Library == libraryId).Select(entry => entry.Entry).ToList();
        return Task.FromResult(CollectionFailure == RemoteFailure.None
            ? RemoteResult.Success(RewriteSharedCollections(new RemoteCollectionPage(all.Skip(startIndex).Take(limit).ToList(), all.Count)))
            : RemoteResult.Fail<RemoteCollectionPage>(CollectionFailure));
    }

    public Task<RemoteResult<RemoteItemPage>> GetCollectionFilmsAsync(RemoteEndpoint endpoint, RemoteSession session, string collectionId, int startIndex, int limit, CancellationToken cancellationToken)
    {
        CollectionReads.Add(collectionId + "@" + startIndex);
        var films = Collections.Where(entry => entry.Collection.Id == collectionId).SelectMany(entry => entry.Films).Select(id => new RemoteItem { Id = id }).ToList();
        return Task.FromResult(CollectionFailure == RemoteFailure.None
            ? RemoteResult.Success(new RemoteItemPage(films.Skip(startIndex).Take(limit).ToList(), films.Count))
            : RemoteResult.Fail<RemoteItemPage>(CollectionFailure));
    }
}
