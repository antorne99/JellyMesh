using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

// The carries done per user and old item, which a later run repeats only after a new watch (decision 155).
public sealed class WatchStateCarryLogTests : IDisposable
{
    private static readonly Guid _user = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _item = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _otherItem = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime _date = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public void NothingIsKnownInAnEmptyDatabase()
    {
        using var log = Log();

        Assert.True(log.EnsureLoaded());
        Assert.False(log.TryFind(_user, _item, out _));
    }

    [Fact]
    public void EachCarryIsWrittenAtOnce_AndSurvivesARestartWithItsDate()
    {
        using (var log = Log())
        {
            log.Record(_user, _item, _date, WatchStateCarryOperation.Merge);
            log.Record(_otherUser, _item, null, WatchStateCarryOperation.Merge);
        }

        Assert.Equal(2, _database.Count("watch_state_carries"));
        using var reloaded = Log();

        Assert.True(reloaded.TryFind(_user, _item, out var date));
        Assert.Equal(_date, date);
        Assert.True(reloaded.TryFind(_otherUser, _item, out var none));
        Assert.Null(none);
        Assert.False(reloaded.TryFind(_user, _otherItem, out _));
    }

    [Fact]
    public void ANewerCarryReplacesTheOneBefore()
    {
        using (var log = Log())
        {
            log.Record(_user, _item, null, WatchStateCarryOperation.Merge);
            log.Record(_user, _item, _date, WatchStateCarryOperation.Merge);
        }

        using var reloaded = Log();

        Assert.True(reloaded.TryFind(_user, _item, out var date));
        Assert.Equal(_date, date);
        Assert.Equal(1, _database.Count("watch_state_carries"));
    }

    [Fact]
    public async Task NothingNewWritesNothing()
    {
        using var log = Log();

        Assert.True(await log.SaveAsync(CancellationToken.None));
        Assert.Equal(0, log.Forget((_, _) => true));

        Assert.Equal(0, _database.Writes);
    }

    [Fact]
    public async Task ForgottenCarriesAreGoneAfterARestart()
    {
        using (var log = Log())
        {
            log.Record(_user, _item, _date, WatchStateCarryOperation.Merge);
            log.Record(_otherUser, _item, _date, WatchStateCarryOperation.Merge);
            log.Record(_user, _otherItem, _date, WatchStateCarryOperation.Merge);

            Assert.Equal(2, log.Forget((user, item) => user == _otherUser || item == _otherItem));
            Assert.False(log.TryFind(_otherUser, _item, out _));
            Assert.True(await log.SaveAsync(CancellationToken.None));
        }

        Assert.Equal(1, _database.Count("watch_state_carries"));
        using var reloaded = Log();

        Assert.True(reloaded.TryFind(_user, _item, out _));
        Assert.False(reloaded.TryFind(_otherUser, _item, out _));
        Assert.False(reloaded.TryFind(_user, _otherItem, out _));
    }

    [Fact]
    public async Task ADatabaseThatCannotBeReadForNowSuspendsTheLogUntilItReads()
    {
        using (var log = Log())
        {
            log.Record(_user, _item, _date, WatchStateCarryOperation.Merge);
        }

        _database.Suspended = true;
        var logger = new CapturingLogger<WatchStateCarryLog>();
        using var reloaded = Log(logger);

        Assert.False(reloaded.EnsureLoaded());
        Assert.False(reloaded.TryFind(_user, _item, out _));
        reloaded.Record(_otherUser, _item, _date, WatchStateCarryOperation.Merge);
        Assert.False(await reloaded.SaveAsync(CancellationToken.None));
        Assert.Contains(logger.Lines, line => line.Contains("StoreSuspendedException", StringComparison.Ordinal));

        _database.Suspended = false;

        Assert.True(reloaded.EnsureLoaded());
        Assert.True(reloaded.TryFind(_user, _item, out var date));
        Assert.Equal(_date, date);
        Assert.False(reloaded.TryFind(_otherUser, _item, out _));
        Assert.Equal(1, _database.Count("watch_state_carries"));
    }

    [Fact]
    public async Task ACarryThatCannotBeWrittenIsReported_AndWrittenByTheNextSave()
    {
        var logger = new CapturingLogger<WatchStateCarryLog>();
        using var log = Log(logger);
        Assert.True(log.EnsureLoaded());
        _database.Suspended = true;

        log.Record(_user, _item, _date, WatchStateCarryOperation.Merge);
        log.Record(_otherUser, _item, _date, WatchStateCarryOperation.Merge);
        Assert.Equal(1, log.Forget((user, _) => user == _otherUser));

        Assert.True(log.TryFind(_user, _item, out _));
        Assert.False(await log.SaveAsync(CancellationToken.None));
        Assert.Single(logger.Lines);

        _database.Suspended = false;
        Assert.True(await log.SaveAsync(CancellationToken.None));
        using var reloaded = Log();
        Assert.True(reloaded.TryFind(_user, _item, out _));
        Assert.False(reloaded.TryFind(_otherUser, _item, out _));
    }

    // Row 575: each record keeps the operation it comes from.
    [Fact]
    public void EachCarrySaysWhichOperationMadeIt_AfterARestart()
    {
        using (var log = Log())
        {
            log.Record(_user, _item, _date, WatchStateCarryOperation.Rename);
            log.Record(_otherUser, _item, null, WatchStateCarryOperation.Migration);
        }

        using var reloaded = Log();

        Assert.True(reloaded.TryFind(_user, _item, out var date, out var operation));
        Assert.Equal((_date, WatchStateCarryOperation.Rename), (date!.Value, operation));
        Assert.True(reloaded.TryFind(_otherUser, _item, out _, out var other));
        Assert.Equal(WatchStateCarryOperation.Migration, other);
    }

    [Fact]
    public void ARenameMarksTheRecord_AndALaterCarryOfAnotherOperationKeepsTheMark()
    {
        using (var log = Log())
        {
            log.Record(_user, _item, null, WatchStateCarryOperation.Merge);
            log.Record(_user, _item, null, WatchStateCarryOperation.Rename);
            log.Record(_user, _item, _date, WatchStateCarryOperation.Merge);
        }

        using var reloaded = Log();

        Assert.True(reloaded.TryFind(_user, _item, out var date, out var operation));
        Assert.Equal((_date, WatchStateCarryOperation.Rename), (date!.Value, operation));
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("'Teleport'")]
    [InlineData("'1'")]
    public async Task ARecordOfNoKnownOperationReadsAsUnknown_AndKeepsItAfterAMerge(string operation)
    {
        await _database.ExecuteAsync($"INSERT INTO watch_state_carries (user_id, item_id, last_played_ticks, operation) VALUES ('{_user:N}', '{_item:N}', NULL, {operation});");
        using var log = Log();

        Assert.True(log.TryFind(_user, _item, out _, out var read));
        Assert.Null(read);
        log.Record(_user, _item, _date, WatchStateCarryOperation.Merge);
        Assert.True(log.TryFind(_user, _item, out _, out var kept));
        Assert.Null(kept);
    }

    // A carry recorded before row 575 has no operation: it reads as unknown after the schema step.
    [Fact]
    public async Task ACarryOfAnOlderSchemaReadsAsOfNoKnownOperation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "jellymesh-carries-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var older = new PluginDatabase(folder, new CapturingLogger<PluginDatabase>(), KeyFileGuards.ForThisSystem(), SchemaMigrations.All.Where(step => step.Version <= 8).ToList(), 1))
            {
                await older.WriteAsync(
                    (connection, transaction) => StoreSql.Execute(connection, transaction, "INSERT INTO watch_state_carries (user_id, item_id, last_played_ticks) VALUES ($user, $item, $played);", ("$user", StoreSql.Text(_user)), ("$item", StoreSql.Text(_item)), ("$played", _date.Ticks)),
                    CancellationToken.None);
            }

            using var database = new TestDatabase(folder);
            using var log = new WatchStateCarryLog(database, new CapturingLogger<WatchStateCarryLog>());

            Assert.True(log.TryFind(_user, _item, out var date, out var operation));
            Assert.Equal(_date, date);
            Assert.Null(operation);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public async Task TheStepOfTheOperationsLeavesADatabaseThatHasThemAsItIs()
    {
        await _database.WriteAsync(SmallStateSchema.AddCarryOperations, CancellationToken.None);

        using var log = Log();
        log.Record(_user, _item, _date, WatchStateCarryOperation.Rename);
        Assert.True(log.TryFind(_user, _item, out _, out var operation));
        Assert.Equal(WatchStateCarryOperation.Rename, operation);
    }

    private WatchStateCarryLog Log(CapturingLogger<WatchStateCarryLog>? logger = null) => new(_database, logger ?? new CapturingLogger<WatchStateCarryLog>());

    // A database opened again, as from a copy, may hold other carries: they are read again before use.
    [Fact]
    public async Task AfterTheDatabaseIsOpenedAgainTheCarriesAreReadAgain()
    {
        using var log = new WatchStateCarryLog(_database, new CapturingLogger<WatchStateCarryLog>());
        log.Record(_user, _item, _date, WatchStateCarryOperation.Merge);
        await _database.ExecuteAsync("DELETE FROM watch_state_carries;");

        _database.Reopen();

        Assert.False(log.TryFind(_user, _item, out _));
    }
}
