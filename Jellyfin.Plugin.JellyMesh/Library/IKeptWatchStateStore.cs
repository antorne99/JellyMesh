using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The watch state of the titles of removed servers, in a SQLite database in the plugin data folder (Q412, Q414).
/// </summary>
public interface IKeptWatchStateStore
{
    /// <summary>
    /// Gets a value indicating whether the file exists, so that a server that never removed one makes none.
    /// </summary>
    bool Exists { get; }

    /// <summary>
    /// Saves the titles in one transaction, each replacing the record of the same old item.
    /// </summary>
    /// <param name="titles">The titles with their users.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveAsync(IReadOnlyCollection<KeptTitle> titles, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every title with its users.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The titles.</returns>
    Task<IReadOnlyList<KeptTitle>> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes these users' state of these titles, then the titles left with no user.
    /// </summary>
    /// <param name="rows">The title and user of each state put back.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ForgetAsync(IReadOnlyCollection<(long TitleId, Guid UserId)> rows, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the titles saved before a time and the state of users no longer there, then the titles left with no user.
    /// </summary>
    /// <param name="savedBeforeUtc">The oldest save kept.</param>
    /// <param name="users">The local users. Empty means they cannot be read now, so every user's state stays.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many titles went.</returns>
    Task<int> PruneAsync(DateTime savedBeforeUtc, IReadOnlySet<Guid> users, CancellationToken cancellationToken);
}
