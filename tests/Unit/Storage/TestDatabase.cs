using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

/// <summary>
/// A plugin database at the latest schema in a temp folder of its own, which can be suspended to stand for one that
/// cannot be opened.
/// </summary>
internal sealed class TestDatabase : IPluginDatabase, IDisposable
{
    private readonly PluginDatabase _database;

    // Under the temp folder, not the bind mount: WAL needs shared memory a host mount may not give.
    public TestDatabase()
    {
        Folder = Path.Combine(Path.GetTempPath(), "jellymesh-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        _database = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>(), KeyFileGuards.ForThisSystem(), SchemaMigrations.All, 1);
    }

    public string Folder { get; }

    /// <summary>Gets or sets a value indicating whether every read and write throws as on a suspended store.</summary>
    public bool Suspended { get; set; }

    /// <summary>Gets how many writes were committed.</summary>
    public int Writes { get; private set; }

    public StoreHealth Health => Suspended ? new StoreHealth(StoreState.Unavailable, "Suspended by the test.") : _database.Health;

    public StoreHealth Retry() => _database.Retry();

    public Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken)
    {
        ThrowIfSuspended();
        return _database.ReadAsync(read, cancellationToken);
    }

    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
    {
        ThrowIfSuspended();
        var result = await _database.WriteAsync(write, cancellationToken).ConfigureAwait(false);
        Writes++;
        return result;
    }

    public Task WriteAsync(Action<SqliteConnection, SqliteTransaction> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return WriteAsync(
            (connection, transaction) =>
            {
                write(connection, transaction);
                return 0;
            },
            cancellationToken);
    }

    public Task<bool> TruncateWalAsync(CancellationToken cancellationToken) => _database.TruncateWalAsync(cancellationToken);

    /// <summary>Counts the rows of a table.</summary>
    public long Count(string table) => _database.ReadAsync(
        (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
#pragma warning disable CA2100 // A table name of the test.
            command.CommandText = "SELECT count(*) FROM " + table + ";";
#pragma warning restore CA2100
            return (long)command.ExecuteScalar()!;
        },
        CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, true);
        }
    }

    private void ThrowIfSuspended()
    {
        if (Suspended)
        {
            throw new StoreSuspendedException("Suspended by the test.");
        }
    }
}
