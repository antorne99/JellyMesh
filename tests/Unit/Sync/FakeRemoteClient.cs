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

    public Task<RemoteResult<RemoteSession>> AuthenticateAsync(RemoteEndpoint endpoint, RemoteCredentials credentials, CancellationToken cancellationToken)
    {
        Logins++;
        return Task.FromResult(RemoteResult.Success(new RemoteSession(TestValues.Token, "abc")));
    }

    public Task<RemoteResult<RemoteCapabilities>> DetectCapabilitiesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
        => Task.FromResult(RemoteResult.Success(new RemoteCapabilities(RemoteMode.Standard, 0)));

    public Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> GetLibrariesAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
        => Task.FromResult(RemoteResult.Success<IReadOnlyList<RemoteLibrary>>(Libraries.ToList()));

    public Task<RemoteResult<DateTimeOffset?>> GetServerTimeAsync(RemoteEndpoint endpoint, RemoteSession session, CancellationToken cancellationToken)
    {
        ClockReads++;
        return Task.FromResult(ClockFailure == RemoteFailure.None ? RemoteResult.Success(ServerTime) : RemoteResult.Fail<DateTimeOffset?>(ClockFailure));
    }

    /// <summary>The images requested, as item/type/index.</summary>
    public List<string> ImageReads { get; } = [];

    /// <summary>Fails the image reads.</summary>
    public RemoteFailure ImageFailure { get; set; }

    public Task<RemoteResult<RemoteImage>> GetImageAsync(RemoteEndpoint endpoint, RemoteSession session, string itemId, string imageType, int imageIndex, CancellationToken cancellationToken)
    {
        ImageReads.Add(itemId + "/" + imageType + "/" + imageIndex);
        return Task.FromResult(ImageFailure == RemoteFailure.None
            ? RemoteResult.Success(new RemoteImage(new byte[] { 0xFF, 0xD8, 0xFF }, "image/jpeg"))
            : RemoteResult.Fail<RemoteImage>(ImageFailure));
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
            var found = ItemsById.Where(item => ids.Contains(item.Id)).ToList();
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
}
