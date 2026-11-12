using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Library;
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

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-carry-log-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, WatchStateCarryLog.FileName);

    public void Dispose()
    {
        if (File.Exists(_folder))
        {
            File.Delete(_folder);
        }
        else if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public void NothingIsKnownWithoutAFile()
    {
        using var log = Log();

        Assert.False(log.TryFind(_user, _item, out _));
    }

    [Fact]
    public async Task EachCarrySurvivesARestartWithItsDate()
    {
        using (var log = Log())
        {
            log.Record(_user, _item, _date);
            log.Record(_otherUser, _item, null);
            Assert.True(await log.SaveAsync(CancellationToken.None));
        }

        using var reloaded = Log();

        Assert.True(reloaded.TryFind(_user, _item, out var date));
        Assert.Equal(_date, date);
        Assert.True(reloaded.TryFind(_otherUser, _item, out var none));
        Assert.Null(none);
        Assert.False(reloaded.TryFind(_user, _otherItem, out _));
    }

    [Fact]
    public async Task AFailedSaveIsReportedAndTriedAgain()
    {
        await File.WriteAllTextAsync(_folder, "in the way");
        var logger = new CapturingLogger<WatchStateCarryLog>();
        using var log = Log(logger);
        log.Record(_user, _item, _date);

        Assert.False(await log.SaveAsync(CancellationToken.None));
        Assert.NotEmpty(logger.Lines);
        Assert.True(log.TryFind(_user, _item, out _));

        File.Delete(_folder);
        Assert.True(await log.SaveAsync(CancellationToken.None));
        using var reloaded = Log();
        Assert.True(reloaded.TryFind(_user, _item, out _));
    }

    [Fact]
    public async Task NothingNewWritesNoFile()
    {
        using var log = Log();

        Assert.True(await log.SaveAsync(CancellationToken.None));

        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":99,\"Entries\":[]}")]
    [InlineData("{\"Version\":1,\"Entries\":[null]}")]
    public void ABadFileIsSetAside(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);

        using var log = Log();

        Assert.False(log.TryFind(_user, _item, out _));
        Assert.Equal(content, File.ReadAllText(FilePath + ".damaged"));
    }

    [Fact]
    public async Task ForgottenCarriesAreGoneAfterARestart()
    {
        using (var log = Log())
        {
            log.Record(_user, _item, _date);
            log.Record(_otherUser, _item, _date);
            log.Record(_user, _otherItem, _date);
            Assert.True(await log.SaveAsync(CancellationToken.None));

            Assert.Equal(2, log.Forget((user, item) => user == _otherUser || item == _otherItem));
            Assert.False(log.TryFind(_otherUser, _item, out _));
            Assert.True(await log.SaveAsync(CancellationToken.None));
        }

        using var reloaded = Log();

        Assert.True(reloaded.TryFind(_user, _item, out _));
        Assert.False(reloaded.TryFind(_otherUser, _item, out _));
        Assert.False(reloaded.TryFind(_user, _otherItem, out _));
    }

    [Fact]
    public async Task ForgettingNothingWritesNoFile()
    {
        using var log = Log();

        Assert.Equal(0, log.Forget((_, _) => true));
        Assert.True(await log.SaveAsync(CancellationToken.None));

        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task AFileThatCannotBeReadForNowSuspendsTheLogUntilItReads(Type error)
    {
        using (var log = Log())
        {
            log.Record(_user, _item, _date);
            Assert.True(await log.SaveAsync(CancellationToken.None));
        }

        var content = await File.ReadAllTextAsync(FilePath);
        var failing = true;
        var logger = new CapturingLogger<WatchStateCarryLog>();
        using var reloaded = new WatchStateCarryLog(
            _folder,
            logger,
            path => failing ? throw (Exception)Activator.CreateInstance(error, "denied /secret")! : File.OpenRead(path));

        Assert.False(reloaded.EnsureLoaded());
        Assert.False(reloaded.TryFind(_user, _item, out _));
        reloaded.Record(_otherUser, _item, _date);
        Assert.False(await reloaded.SaveAsync(CancellationToken.None));
        Assert.Equal(content, await File.ReadAllTextAsync(FilePath));
        Assert.False(File.Exists(FilePath + ".damaged"));
        Assert.Contains(logger.Lines, line => line.Contains(error.Name, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("secret", StringComparison.Ordinal));

        failing = false;

        Assert.True(reloaded.EnsureLoaded());
        Assert.True(reloaded.TryFind(_user, _item, out var date));
        Assert.Equal(_date, date);
        Assert.False(reloaded.TryFind(_otherUser, _item, out _));
    }

    [Fact]
    public async Task NoTemporaryFileStays()
    {
        using var log = Log();
        log.Record(_user, _item, _date);

        await log.SaveAsync(CancellationToken.None);

        Assert.Equal([WatchStateCarryLog.FileName], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    private WatchStateCarryLog Log(CapturingLogger<WatchStateCarryLog>? logger = null) => new(_folder, logger ?? new CapturingLogger<WatchStateCarryLog>());
}
