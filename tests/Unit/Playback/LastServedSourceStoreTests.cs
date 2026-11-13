using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Playback;

public sealed class LastServedSourceStoreTests : IDisposable
{
    private static readonly Guid _user = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherUser = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _server = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _otherServer = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "jellymesh-last-served-" + Guid.NewGuid().ToString("N"));
    private readonly StepClock _clock = new();

    private string FilePath => Path.Combine(_folder, LastServedSourceStore.FileName);

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

    private LastServedSourceStore Store(CapturingLogger<LastServedSourceStore>? logger = null) => new(_folder, logger ?? new CapturingLogger<LastServedSourceStore>(), _clock);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 200 && !condition(); waited++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public void NothingIsKnown_WithoutAFile()
    {
        using var store = Store();

        Assert.Null(store.Find(_user, "stub"));
    }

    [Fact]
    public async Task ARecord_IsKnownAtOnce_ButNotWrittenBeforeTheDelay()
    {
        using var store = Store();

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        _clock.Advance(LastServedSourceStore.SaveDelay - TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Equal(_server, store.Find(_user, "stub"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task ARecord_IsWrittenOnceTheDelayPasses()
    {
        using var store = Store();

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        _clock.Advance(LastServedSourceStore.SaveDelay);
        await WaitForAsync(() => File.Exists(FilePath));

        using var reloaded = Store();
        Assert.Equal(_server, reloaded.Find(_user, "stub"));
    }

    [Fact]
    public async Task RecordsThatKeepComing_DoNotPutTheWriteOff()
    {
        using var store = Store();
        var half = LastServedSourceStore.SaveDelay / 2;

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        _clock.Advance(half);
        await store.RecordAsync(_user, "other-stub", _otherServer, CancellationToken.None);
        _clock.Advance(half);
        await WaitForAsync(() => File.Exists(FilePath));

        using var reloaded = Store();
        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_user, "other-stub"));
    }

    [Fact]
    public async Task AClosedStore_WritesWhatIsPending()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_server, reloaded.Find(_user, "stub"));
    }

    [Fact]
    public async Task AStoppingServer_WritesWhatIsPending()
    {
        using var store = Store();
        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);

        await store.StopAsync(CancellationToken.None);

        using var reloaded = Store();
        Assert.Equal(_server, reloaded.Find(_user, "stub"));
    }

    [Fact]
    public async Task EachUserKeepsTheirOwnServer_PerStub()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
            await store.RecordAsync(_otherUser, "stub", _otherServer, CancellationToken.None);
            await store.RecordAsync(_user, "other-stub", _otherServer, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_otherUser, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_user, "other-stub"));
        Assert.Null(reloaded.Find(_otherUser, "other-stub"));
    }

    [Fact]
    public async Task TheLatestServerReplacesTheOneBefore()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
            await store.RecordAsync(_user, "stub", _otherServer, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_otherServer, reloaded.Find(_user, "stub"));
    }

    [Fact]
    public async Task TheSameServerAgain_DoesNotRewriteTheFile()
    {
        using var store = Store();
        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath, written);

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(written, File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public async Task NoTemporaryFileStays()
    {
        using var store = Store();

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal([LastServedSourceStore.FileName], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Version\":99,\"Entries\":[]}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Entries\":[{\"UserId\":\"11111111-1111-1111-1111-111111111111\"}]}")]
    [InlineData("{\"Version\":1,\"Entries\":[null]}")]
    public void ABadFileIsSetAside(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);

        using var store = Store();

        Assert.Null(store.Find(_user, "stub"));
        Assert.Equal(content, File.ReadAllText(FilePath + ".damaged"));
    }

    [Fact]
    public async Task AFailedSave_IsLoggedAndKeptInMemory()
    {
        // A file where the folder should be: nothing can be written under it.
        await File.WriteAllTextAsync(_folder, "in the way");
        var logger = new CapturingLogger<LastServedSourceStore>();
        using var store = Store(logger);

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(_server, store.Find(_user, "stub"));
        Assert.NotEmpty(logger.Lines);
    }

    [Fact]
    public async Task AFailedSave_IsTriedAgainWithTheSameServer()
    {
        await File.WriteAllTextAsync(_folder, "in the way");
        using var store = Store();
        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);
        File.Delete(_folder);

        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        _clock.Advance(LastServedSourceStore.SaveDelay);
        await WaitForAsync(() => File.Exists(FilePath));

        using var reloaded = Store();
        Assert.Equal(_server, reloaded.Find(_user, "stub"));
    }

    [Fact]
    public async Task ARecordAfterARestart_KeepsTheOnesBefore()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        }

        using (var restarted = Store())
        {
            await restarted.RecordAsync(_otherUser, "stub", _otherServer, CancellationToken.None);
        }

        using var reloaded = Store();

        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Equal(_otherServer, reloaded.Find(_otherUser, "stub"));
    }

    [Fact]
    public async Task ARepeatedEntry_DoesNotStopTheStore()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(
            FilePath,
            "{\"Version\":1,\"Entries\":["
            + "{\"UserId\":\"" + _user + "\",\"StubId\":\"stub\",\"ServerId\":\"" + _server + "\"},"
            + "{\"UserId\":\"" + _user + "\",\"StubId\":\"stub\",\"ServerId\":\"" + _otherServer + "\"}]}");

        using var store = Store();

        Assert.Equal(_otherServer, store.Find(_user, "stub"));
    }

    [Fact]
    public async Task TheFileNamesTheServerById_AndHoldsNothingElseOfIt()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        }

        var text = await File.ReadAllTextAsync(FilePath);

        Assert.Contains(_server.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task APrune_DropsTheStubsThatAreGone_AndKeepsTheOthers()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "gone", _server, CancellationToken.None);
            await store.RecordAsync(_otherUser, "gone", _otherServer, CancellationToken.None);
            await store.RecordAsync(_user, "kept", _server, CancellationToken.None);

            var pruned = store.Prune(stubId => stubId == "kept", new HashSet<Guid>());

            Assert.Equal(2, pruned);
            Assert.Null(store.Find(_user, "gone"));
        }

        using var reloaded = Store();
        Assert.Null(reloaded.Find(_user, "gone"));
        Assert.Null(reloaded.Find(_otherUser, "gone"));
        Assert.Equal(_server, reloaded.Find(_user, "kept"));
    }

    [Fact]
    public async Task APrune_DropsTheServersRemoved_AlsoOnStubsThatStay()
    {
        using (var store = Store())
        {
            await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
            await store.RecordAsync(_otherUser, "stub", _otherServer, CancellationToken.None);

            store.Prune(_ => true, new HashSet<Guid> { _otherServer });
        }

        using var reloaded = Store();
        Assert.Equal(_server, reloaded.Find(_user, "stub"));
        Assert.Null(reloaded.Find(_otherUser, "stub"));
    }

    [Fact]
    public async Task APruneWithNothingToDrop_DoesNotRewriteTheFile()
    {
        using var store = Store();
        await store.RecordAsync(_user, "stub", _server, CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath, written);

        Assert.Equal(0, store.Prune(_ => true, new HashSet<Guid>()));
        await store.FlushAsync(CancellationToken.None);

        Assert.Equal(written, File.GetLastWriteTimeUtc(FilePath));
    }

    // A clock whose timers fire when the test moves it past their due time.
    private sealed class StepClock : TimeProvider
    {
        private readonly List<StepTimer> _timers = [];
        private TimeSpan _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new StepTimer(this, callback, state);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan time)
        {
            List<StepTimer> due;
            lock (_timers)
            {
                _now += time;
                due = _timers.Where(timer => timer.Due is { } at && at <= _now).ToList();
                foreach (var timer in due)
                {
                    timer.Due = null;
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class StepTimer(StepClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._timers)
                {
                    clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
