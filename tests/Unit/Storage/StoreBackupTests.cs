using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Storage;

public sealed class StoreBackupTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly List<PluginDatabase> _opened = [];

    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero));

    public StoreBackupTests() => Directory.CreateDirectory(Folder);

    // Under the temp folder, not the bind mount: WAL needs shared memory a host mount may not give.
    private string Folder { get; } = Path.Combine(Path.GetTempPath(), "jellymesh-backups-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(Folder, PluginDatabase.FileName);

    public void Dispose()
    {
        foreach (var database in _opened)
        {
            database.Dispose();
        }

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, true);
        }
    }

    private PluginDatabase Open(IKeyFileGuard? guard = null)
    {
        var database = new PluginDatabase(
            Folder,
            new CapturingLogger<PluginDatabase>(),
            guard ?? KeyFileGuards.ForThisSystem(),
            SchemaMigrations.All,
            1,
            _clock);
        _opened.Add(database);
        return database;
    }

    private static object? Scalar(SqliteConnection connection, string text, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command.ExecuteScalar();
    }

    private static Task AddItemAsync(PluginDatabase database, string value) => database.WriteAsync(
        (connection, transaction) =>
        {
            Scalar(connection, "CREATE TABLE IF NOT EXISTS items (value TEXT);", transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO items VALUES ($value);";
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        },
        CancellationToken.None);

    private static List<string> ReadItems(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM items ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private string[] Copies() => Directory.GetFiles(Folder, PluginDatabase.FileName + ".backup-*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task ACopyHoldsTheDatabaseUnderTheNameOfItsTime()
    {
        var database = Open();
        await AddItemAsync(database, "kept");

        var copy = await database.BackUpAsync(CancellationToken.None);

        Assert.Equal(Path.Combine(Folder, "jellymesh.db.backup-20261118030000"), copy.FilePath);
        Assert.Equal(_clock.Now, copy.CreatedAt);
        Assert.Equal(["kept"], ReadItems(copy.FilePath));
    }

    // VACUUM INTO makes its file 0644 (F23), so the store makes it private first.
    [Fact]
    public async Task ACopyIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var database = Open();

        var copy = await database.BackUpAsync(CancellationToken.None);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(copy.FilePath));
    }

    [Fact]
    public async Task OnlyTheLastTwoCopiesAreKept()
    {
        var database = Open();
        for (var night = 0; night < 3; night++)
        {
            await AddItemAsync(database, "night " + night);
            await database.BackUpAsync(CancellationToken.None);
            _clock.Now += TimeSpan.FromDays(1);
        }

        Assert.Equal(["jellymesh.db.backup-20261119030000", "jellymesh.db.backup-20261120030000"], Copies());
        Assert.Equal(["night 0", "night 1", "night 2"], ReadItems(Path.Combine(Folder, "jellymesh.db.backup-20261120030000")));
    }

    // The key is a file of its own, out of the database, so no copy carries it.
    [Fact]
    public async Task ACopyHoldsNoKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await File.WriteAllBytesAsync(Path.Combine(Folder, "credential.key"), key);
        var database = Open();
        await AddItemAsync(database, "encrypted credentials");

        var copy = await database.BackUpAsync(CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(copy.FilePath);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(key));
    }

    [Fact]
    public async Task ACopyOfTheSameSecondReplacesTheEarlierOne()
    {
        var database = Open();
        await database.BackUpAsync(CancellationToken.None);
        await AddItemAsync(database, "later");

        var copy = await database.BackUpAsync(CancellationToken.None);

        Assert.Equal(["jellymesh.db.backup-20261118030000"], Copies());
        Assert.Equal(["later"], ReadItems(copy.FilePath));
    }

    // A copy that fails leaves no empty file a recovery would take for the latest one, and the copies before it stay.
    [Fact]
    public async Task AFailedCopyLeavesNothingAndKeepsTheCopiesBefore()
    {
        var guard = new SpoilingGuard();
        var database = Open(guard);
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromDays(1);
        await database.BackUpAsync(CancellationToken.None);
        _clock.Now += TimeSpan.FromDays(1);

        guard.Spoil = true;
        await Assert.ThrowsAsync<SqliteException>(() => database.BackUpAsync(CancellationToken.None));

        Assert.Equal(["jellymesh.db.backup-20261118030000", "jellymesh.db.backup-20261119030000"], Copies());
        Assert.Equal(StoreState.Healthy, database.Health.State);
    }

    [Fact]
    public async Task ASuspendedDatabaseMakesNoCopy()
    {
        Directory.CreateDirectory(DatabasePath);
        var database = Open();

        await Assert.ThrowsAsync<StoreSuspendedException>(() => database.BackUpAsync(CancellationToken.None));

        Assert.Empty(Copies());
    }

    // Fills each new file once spoiled, which VACUUM INTO refuses (F23).
    private sealed class SpoilingGuard : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = KeyFileGuards.ForThisSystem();

        public bool Spoil { get; set; }

        public FileStream CreatePrivate(string path)
        {
            var stream = _inner.CreatePrivate(path);
            if (Spoil)
            {
                stream.Write(new byte[8192]);
                stream.Flush();
            }

            return stream;
        }

        public void KeepPrivate(string path) => _inner.KeepPrivate(path);
    }
}
