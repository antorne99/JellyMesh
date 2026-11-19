using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// The plugin database, <c>jellymesh.db</c>: one writer at a time, readers on their own connections.
/// </summary>
public interface IPluginDatabase
{
    /// <summary>
    /// Gets the state of the database, opening it on the first read.
    /// </summary>
    StoreHealth Health { get; }

    /// <summary>
    /// Gets a number that changes each time the database is opened healthy, as after a retry or a restore from a copy, so
    /// that rows read before can be told stale. Reading it never opens the database.
    /// </summary>
    long Generation { get; }

    /// <summary>
    /// Gets the last time a damaged database was set aside and replaced, which the page shows with the copy date until the
    /// admin dismisses it; <c>null</c> when there is none. It opens the database on the first read.
    /// </summary>
    StoreRecovery? Recovery { get; }

    /// <summary>
    /// Forgets the last recovery once the admin has checked the server list.
    /// </summary>
    void DismissRecovery();

    /// <summary>
    /// Opens the database again when it is not healthy, as the page's "Retry" asks.
    /// </summary>
    /// <returns>The state after the attempt.</returns>
    StoreHealth Retry();

    /// <summary>
    /// Runs a read on a connection of its own, inside a read transaction, so it sees one snapshot and never blocks the writer.
    /// </summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="read">The read, which sets the transaction on its commands.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the read returned.</returns>
    /// <exception cref="StoreSuspendedException">The database is not healthy.</exception>
    Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a write in a short <c>IMMEDIATE</c> transaction on the only writer connection, after the writes before it.
    /// It commits when <paramref name="write"/> returns and rolls back when it throws.
    /// </summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="write">The write, which sets the transaction on its commands.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the write returned.</returns>
    /// <exception cref="StoreSuspendedException">The database is not healthy.</exception>
    Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a write in a short <c>IMMEDIATE</c> transaction on the only writer connection, after the writes before it.
    /// </summary>
    /// <param name="write">The write, which sets the transaction on its commands.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the write is committed.</returns>
    /// <exception cref="StoreSuspendedException">The database is not healthy.</exception>
    Task WriteAsync(Action<SqliteConnection, SqliteTransaction> write, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the write-ahead log into the file and empties it, so deleted secrets do not stay readable in <c>-wal</c>.
    /// Call it after deleting credentials.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> when the log was emptied; <c>false</c> when a reader kept it busy.</returns>
    /// <exception cref="StoreSuspendedException">The database is not healthy.</exception>
    Task<bool> TruncateWalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Copies the database with <c>VACUUM INTO</c> into an owner-only file next to it, after the writes before it, and keeps
    /// the last 2 copies, overwriting the others before deleting them, as the nightly reconciliation asks.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new copy.</returns>
    /// <exception cref="StoreSuspendedException">The database is not healthy.</exception>
    Task<StoreBackup> BackUpAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Overwrites and deletes the damaged files set aside, with their logs, 30 days after the date in their name (D35).
    /// A file that cannot go is logged and left for the next night.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the sweep is done.</returns>
    /// <exception cref="StoreSuspendedException">The database is not healthy, so its damaged files may still be needed.</exception>
    Task DeleteExpiredDamagedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Overwrites and deletes the files of the older version kept in <c>migrated-0.5/</c>, once 30 days have passed since
    /// the migration and a fresh integrity check of the database passes (D25).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the folder is gone, or never was.</returns>
    /// <exception cref="StoreSuspendedException">The store is suspended.</exception>
    Task<bool> DeleteExpiredMigratedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets until when the files of the older version stay in <c>migrated-0.5/</c>, for the page (row 469).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>30 days after the migration while the folder is there; <c>null</c> otherwise, or when it is a link.</returns>
    /// <exception cref="StoreSuspendedException">The store is suspended.</exception>
    Task<DateTimeOffset?> MigratedKeptUntilAsync(CancellationToken cancellationToken);
}
