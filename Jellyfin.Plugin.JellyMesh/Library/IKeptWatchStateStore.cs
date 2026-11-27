using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Storage;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The watch state of the titles of removed servers, in the plugin database (Q412, Q414).
/// </summary>
public interface IKeptWatchStateStore
{
    /// <summary>
    /// Gets the opening of the database; the numbers of the titles of an earlier one may be those of other titles.
    /// </summary>
    long Generation { get; }

    /// <summary>
    /// Tells whether any title is kept, so that a server that never removed one writes nothing for it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether a title is kept.</returns>
    Task<bool> HasTitlesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves the titles in one transaction, each replacing the record of the same old item but keeping its first saved date.
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
    /// <param name="generation">The <see cref="Generation"/> the titles were read in; another one now deletes nothing.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="StoreSuspendedException">The database was opened again since the titles were read.</exception>
    Task ForgetAsync(IReadOnlyCollection<(long TitleId, Guid UserId)> rows, long generation, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the titles saved before a time and the state of users no longer there, then the titles left with no user.
    /// </summary>
    /// <param name="savedBeforeUtc">The oldest save kept.</param>
    /// <param name="users">The local users. Empty means they cannot be read now, so every user's state stays.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many titles went.</returns>
    Task<int> PruneAsync(DateTime savedBeforeUtc, IReadOnlySet<Guid> users, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the manual merges a removed server had a copy in, before its copies leave them, in place of those saved for it.
    /// </summary>
    /// <param name="serverId">The removed server.</param>
    /// <param name="merges">Its merges, with all their copies.</param>
    /// <param name="savedUtc">When, in UTC: they go with the titles kept as long.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task SaveMergesAsync(Guid serverId, IReadOnlyList<ManualMerge> merges, DateTime savedUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the manual merges saved for removed servers.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The merges, each with its removed server and dated when it was saved.</returns>
    Task<IReadOnlyList<(Guid ServerId, ManualMerge Merge)>> ReadMergesAsync(CancellationToken cancellationToken);
}
