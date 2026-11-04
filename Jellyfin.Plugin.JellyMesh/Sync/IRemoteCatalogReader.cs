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
    /// Reads the clock of a server. A read must be stamped with it, because a change is filtered by the time on the remote.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The time on the server, null if it does not tell or there is nothing to read from it.</returns>
    Task<RemoteResult<DateTimeOffset?>> GetServerTimeAsync(RemoteServerInfo server, CancellationToken cancellationToken);
}
