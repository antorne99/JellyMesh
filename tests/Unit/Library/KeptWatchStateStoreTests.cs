using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Security;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public sealed class KeptWatchStateStoreTests : IDisposable
{
    private static readonly DateTime _saved = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid _anna = Guid.NewGuid();
    private static readonly Guid _bruno = Guid.NewGuid();

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-kept-" + Guid.NewGuid().ToString("N"));

    public KeptWatchStateStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode Shared = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private string DatabasePath => Path.Combine(_folder, KeptWatchStateStore.FileName);

    private KeptWatchStateStore Store() => new(_folder, new CapturingLogger<KeptWatchStateStore>());

    private static KeptTitle Film(Guid oldItem, string title = "Film", params KeptUserState[] users) => new()
    {
        OldItemId = oldItem,
        Kind = KeptTitleKind.Movie,
        Title = title,
        Year = 2020,
        SavedUtc = _saved,
        Users = users
    };

    private static KeptUserState Watched(Guid user, int count = 1) =>
        new(user, true, count, 0, new DateTime(2026, 10, 2, 20, 30, 15, DateTimeKind.Utc).AddTicks(1234567), false, null);

    // Every field, lists and users included, in a form records compare by value.
    private static string Describe(KeptTitle title) => string.Join(
        '\n',
        (title with { Genres = [], Studios = [], People = [], Users = [] }).ToString(),
        string.Join(',', title.Genres),
        string.Join(',', title.Studios),
        string.Join(',', title.People),
        string.Join('\n', title.Users.OrderBy(user => user.UserId).Select(user => user + " " + user.LastPlayedUtc?.Ticks)));

    [Fact]
    public async Task ATitleComesBackWithItsIdentityAndEveryUser()
    {
        var oldItem = Guid.NewGuid();
        var episode = new KeptTitle
        {
            OldItemId = oldItem,
            Kind = KeptTitleKind.Episode,
            Title = "L'ombra \"del\" vento; DROP TABLE Titles",
            Year = 2019,
            Ids = new ContentIds("12", "tt3", "45"),
            OriginalTitle = "Shadow",
            RunTimeTicks = 26_000_000_000,
            Overview = "Plot",
            Genres = ["Drama", "Mystery"],
            Studios = ["Rai"],
            People = ["Anna", "Bruno"],
            SeasonNumber = 2,
            EpisodeNumber = 3,
            EpisodeNumberEnd = 4,
            SavedUtc = _saved,
            Users =
            [
                new KeptUserState(_anna, false, 0, 9_000_000_000, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), true, 7.5),
                new KeptUserState(_bruno, true, 2, 0, null, false, null)
            ]
        };

        await Store().SaveAsync([episode], CancellationToken.None);
        var read = Assert.Single(await Store().ReadAsync(CancellationToken.None));

        Assert.True(read.Id > 0);
        Assert.Equal(Describe(episode with { Id = read.Id }), Describe(read));
        Assert.All(read.Users.Select(user => user.LastPlayedUtc).OfType<DateTime>(), date => Assert.Equal(DateTimeKind.Utc, date.Kind));
        Assert.Equal(DateTimeKind.Utc, read.SavedUtc.Kind);
    }

    [Fact]
    public async Task SavingTheSameOldItemAgainReplacesItsRecord()
    {
        var oldItem = Guid.NewGuid();
        var store = Store();
        await store.SaveAsync([Film(oldItem, "Film", Watched(_anna), Watched(_bruno))], CancellationToken.None);

        await store.SaveAsync([Film(oldItem, "Film", Watched(_anna, 3))], CancellationToken.None);

        var read = Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Equal(3, Assert.Single(read.Users).PlayCount);
    }

    [Fact]
    public async Task SavingTheSameOldItemAgainKeepsItsFirstSavedDate()
    {
        var oldItem = Guid.NewGuid();
        var store = Store();
        await store.SaveAsync([Film(oldItem, "Film", Watched(_anna))], CancellationToken.None);

        await store.SaveAsync([Film(oldItem, "Film", Watched(_anna, 3)) with { SavedUtc = _saved.AddDays(30) }], CancellationToken.None);

        var read = Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Equal(3, Assert.Single(read.Users).PlayCount);
        Assert.Equal(_saved, read.SavedUtc);
        Assert.Equal(1, await store.PruneAsync(_saved.AddDays(1), new HashSet<Guid> { _anna }, CancellationToken.None));
        Assert.Empty(await store.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ForgettingTheLastUserOfATitleDeletesTheTitle()
    {
        var store = Store();
        await store.SaveAsync([Film(Guid.NewGuid(), "One", Watched(_anna), Watched(_bruno)), Film(Guid.NewGuid(), "Two", Watched(_anna))], CancellationToken.None);
        var titles = await store.ReadAsync(CancellationToken.None);
        var one = titles.Single(title => title.Title == "One");
        var two = titles.Single(title => title.Title == "Two");

        await store.ForgetAsync([(one.Id, _anna), (two.Id, _anna)], CancellationToken.None);

        var left = Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Equal("One", left.Title);
        Assert.Equal(_bruno, Assert.Single(left.Users).UserId);
    }

    [Fact]
    public async Task PruningDeletesOldTitlesAndTheRowsOfUsersWhoAreGone()
    {
        var store = Store();
        await store.SaveAsync(
            [
                Film(Guid.NewGuid(), "Old", Watched(_anna)) with { SavedUtc = _saved.AddDays(-91) },
                Film(Guid.NewGuid(), "Recent", Watched(_anna), Watched(_bruno)),
                Film(Guid.NewGuid(), "Bruno only", Watched(_bruno))
            ],
            CancellationToken.None);

        var pruned = await store.PruneAsync(_saved.AddDays(-90), new HashSet<Guid> { _anna }, CancellationToken.None);

        var left = Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Equal("Recent", left.Title);
        Assert.Equal(_anna, Assert.Single(left.Users).UserId);
        Assert.Equal(2, pruned);
    }

    [Fact]
    public async Task NoUsersKnownKeepsEveryUserRow()
    {
        var store = Store();
        await store.SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        await store.PruneAsync(_saved.AddDays(-90), new HashSet<Guid>(), CancellationToken.None);

        Assert.Single(Assert.Single(await store.ReadAsync(CancellationToken.None)).Users);
    }

    [Fact]
    public async Task ADamagedFileIsSetAsideAndANewOneMade()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, KeptWatchStateStore.FileName), new string('x', 4096));

        var store = Store();
        await store.SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        Assert.Single(await store.ReadAsync(CancellationToken.None));
        Assert.Single(Directory.GetFiles(_folder, KeptWatchStateStore.FileName + ".damaged-*"));
    }

    [Fact]
    public async Task TheFileIsInTheGivenFolder()
    {
        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_folder, KeptWatchStateStore.FileName)));
    }

    // Row 417: the database and its side files are readable and writable by the owner only, as the key file (decision 42).
    [Fact]
    public async Task ANewFileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(DatabasePath));
    }

    [Fact]
    public async Task AFileOfAnEarlierBuildIsMadeOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);
        File.SetUnixFileMode(DatabasePath, Shared);

        Assert.Single(await Store().ReadAsync(CancellationToken.None));

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(DatabasePath));
    }

    // Checked by the guard right after it acts, since SQLite may drop a side file once it opens the database.
    [Fact]
    public async Task TheSideFilesOfAnEarlierBuildAreMadeOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);
        var sides = new[] { "-journal", "-wal", "-shm" }.Select(suffix => DatabasePath + suffix).Append(DatabasePath + ".damaged-20260101000000").ToList();
        foreach (var side in sides)
        {
            await File.WriteAllBytesAsync(side, []);
            File.SetUnixFileMode(side, Shared);
        }

        var guard = new CheckingGuard();
        await new KeptWatchStateStore(_folder, new CapturingLogger<KeptWatchStateStore>(), guard).ReadAsync(CancellationToken.None);

        foreach (var side in sides)
        {
            Assert.Equal(OwnerOnly, guard.Modes.GetValueOrDefault(side));
        }

        Assert.Equal(OwnerOnly, guard.Modes.GetValueOrDefault(DatabasePath));
    }

    // The store keeps SQLite's default rollback journal, which SQLite makes with the mode of the database.
    [Fact]
    public async Task TheJournalOfAWriteIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("delete", await mode.ExecuteScalarAsync());
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "DELETE FROM UserStates;";
        await write.ExecuteNonQueryAsync();

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(DatabasePath + "-journal"));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ADamagedFileIsSetAsideOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await File.WriteAllTextAsync(DatabasePath, new string('x', 4096));
        File.SetUnixFileMode(DatabasePath, Shared);

        await Store().SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(Assert.Single(Directory.GetFiles(_folder, KeptWatchStateStore.FileName + ".damaged-*"))));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(DatabasePath));
    }

    // As with the key file, a database that cannot be kept private is not used: the save fails and the removal waits.
    [Fact]
    public async Task AFileThatCannotBeMadePrivateIsNotUsed()
    {
        var store = new KeptWatchStateStore(_folder, new CapturingLogger<KeptWatchStateStore>(), new RefusingGuard());

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync([Film(Guid.NewGuid(), "Film", Watched(_anna))], CancellationToken.None));

        Assert.False(File.Exists(DatabasePath));
    }

    // The guard of this system, noting the mode each file has right after it.
    [UnsupportedOSPlatform("windows")]
    private sealed class CheckingGuard : IKeyFileGuard
    {
        private readonly IKeyFileGuard _inner = new UnixKeyFileGuard();

        public Dictionary<string, UnixFileMode> Modes { get; } = new(StringComparer.Ordinal);

        public FileStream CreatePrivate(string path)
        {
            var stream = _inner.CreatePrivate(path);
            Modes[path] = File.GetUnixFileMode(path);
            return stream;
        }

        public void KeepPrivate(string path)
        {
            _inner.KeepPrivate(path);
            Modes[path] = File.GetUnixFileMode(path);
        }
    }

    private sealed class RefusingGuard : IKeyFileGuard
    {
        public FileStream CreatePrivate(string path) => throw new IOException("/secret");

        public void KeepPrivate(string path) => throw new IOException("/secret");
    }
}
