using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Sync;

/// <summary>
/// Reads the catalog of a remote.
/// </summary>
public interface IRemoteCatalogReader
{
    /// <summary>
    /// Reads every entry of the chosen libraries of a server. All or nothing: any failure gives no entries at all, so a
    /// partial read cannot pass for a complete one.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="seenUtc">The time to record as last seen.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entries, or why they could not all be read.</returns>
    Task<RemoteResult<IReadOnlyList<IndexedItem>>> ReadAsync(RemoteServerInfo server, DateTimeOffset seenUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the entries of the chosen libraries saved since a time. All or nothing, like <see cref="ReadAsync"/>.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="sinceUtc">The time to read from, by the clock of the remote.</param>
    /// <param name="knownItemIds">The entries already in the index. A series among them is not looked up again.</param>
    /// <param name="seenUtc">The time to record as last seen.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The entries that changed, or why they could not all be read.</returns>
    Task<RemoteResult<IReadOnlyList<IndexedItem>>> ReadChangesAsync(
        RemoteServerInfo server,
        DateTimeOffset sinceUtc,
        IReadOnlySet<string> knownItemIds,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Learns where the change feed of a JellyMesh remote stands. Asked before a full read, so that what changes during
    /// the read comes up in the next one.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cursor.</returns>
    Task<RemoteResult<string>> GetFeedPositionAsync(RemoteServerInfo server, CancellationToken cancellationToken);

    /// <summary>
    /// Reads what changed on a JellyMesh remote in the chosen libraries. All or nothing, like <see cref="ReadAsync"/>.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="cursor">Where to read from.</param>
    /// <param name="knownItemIds">The entries already in the index. A series among them is not looked up again.</param>
    /// <param name="seenUtc">The time to record as last seen.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The changes, or why they could not all be read.</returns>
    Task<RemoteResult<FeedRead>> ReadFeedAsync(
        RemoteServerInfo server,
        string cursor,
        IReadOnlySet<string> knownItemIds,
        DateTimeOffset seenUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the collections (BoxSets) of the film libraries of a stock server, and the films in each (decision 52).
    /// All or nothing, like <see cref="ReadAsync"/>.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="libraryIds">The film libraries.</param>
    /// <param name="filmIds">The films of the catalog read from the server. Members of a collection not among them are left out.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The collection of each film, by its ID on the server, or why they could not all be read.</returns>
    Task<RemoteResult<IReadOnlyDictionary<string, IndexedBoxSet>>> ReadCollectionsAsync(
        RemoteServerInfo server,
        IReadOnlyCollection<string> libraryIds,
        IReadOnlySet<string> filmIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the clock of a server. A read must be stamped with it, because a change is filtered by the time on the remote.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The time on the server, null if it does not tell or there is nothing to read from it.</returns>
    Task<RemoteResult<DateTimeOffset?>> GetServerTimeAsync(RemoteServerInfo server, CancellationToken cancellationToken);
}
