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
}
