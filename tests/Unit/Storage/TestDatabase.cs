using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

/// <summary>
/// The real plugin database over a folder, which counts its write transactions and can interrupt one or be suspended.
/// </summary>
internal sealed class TestDatabase : IPluginDatabase, IDisposable
{
    // SQLITE_FULL, as a disk that fills up halfway through a write.
    private const int DiskFull = 13;

    private readonly PluginDatabase _inner;
    private int _writes;
    private int _interruptAfter = -1;
    private int _inFlight;
    private volatile bool _disposed;

    public TestDatabase(string folder)
    {
        Folder = folder;
        _inner = new PluginDatabase(folder, new CapturingLogger<PluginDatabase>(), KeyFileGuards.ForThisSystem(), SchemaMigrations.All, 1);
    }

    public string Folder { get; }

    /// <summary>Gets how many write transactions were started.</summary>
    public int Writes => Volatile.Read(ref _writes);

    /// <summary>Gets or sets a value indicating whether the store answers as suspended.</summary>
    public bool Suspended { get; set; }

    public StoreHealth Health => Suspended ? new StoreHealth(StoreState.Unavailable, "test") : _inner.Health;

    /// <summary>Makes the next write fail after its callback ran, so that it rolls back what it wrote.</summary>
    /// <param name="exception">What the write throws; a disk-full error if null.</param>
    public void InterruptNextWrite(Exception? exception = null)
    {
        Interrupt = exception ?? new SqliteException("disk full", DiskFull);
        Volatile.Write(ref _interruptAfter, Writes);
    }

    private Exception? Interrupt { get; set; }

    public StoreHealth Retry() => _inner.Retry();

    public Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken)
    {
        ThrowIfSuspended();
        return _inner.ReadAsync(read, cancellationToken);
    }

    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            ThrowIfSuspended();
            var number = Interlocked.Increment(ref _writes) - 1;
            if (Interlocked.CompareExchange(ref _interruptAfter, -1, number) != number)
            {
                return await _inner.WriteAsync(write, cancellationToken);
            }

            var interrupt = Interrupt!;
            return await _inner.WriteAsync<T>(
                (connection, transaction) =>
                {
                    write(connection, transaction);
                    throw interrupt;
                },
                cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
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

    /// <summary>Reads every value of every row of the tables whose name starts with a prefix, as text.</summary>
    /// <param name="prefix">The prefix of the table names.</param>
    /// <returns>The values, one per line.</returns>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The table names come from the schema of a test database.")]
    public Task<string> DumpAsync(string prefix) => _inner.ReadAsync(
        (connection, transaction) =>
        {
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, length($prefix)) = $prefix ORDER BY name;";
                command.Parameters.AddWithValue("$prefix", prefix);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            var text = new StringBuilder();
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT * FROM \"" + table + "\";";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        text.Append(reader.GetValue(column)).Append('\n');
                    }
                }
            }

            return text.ToString();
        },
        CancellationToken.None);

    public Task<bool> TruncateWalAsync(CancellationToken cancellationToken) => _inner.TruncateWalAsync(cancellationToken);

    // A sync left running by a test may still be writing: let it finish, and refuse what comes after.
    public void Dispose()
    {
        _disposed = true;
        SpinWait.SpinUntil(() => Volatile.Read(ref _inFlight) == 0, TimeSpan.FromSeconds(10));
        _inner.Dispose();
    }

    private void ThrowIfSuspended()
    {
        if (Suspended || _disposed)
        {
            throw new StoreSuspendedException("suspended for the test");
        }
    }
}
