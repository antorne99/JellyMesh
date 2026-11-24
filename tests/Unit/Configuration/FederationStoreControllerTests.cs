using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>The state of the plugin database on the page: its reason and Retry, the restore banner and the old files (rows 427, 466, 469).</summary>
public sealed class FederationStoreControllerTests
{
    private static readonly DateTimeOffset _recovered = new(2026, 11, 18, 3, 5, 0, TimeSpan.Zero);

    private readonly Mock<IPluginDatabase> _database = new();
    private readonly Mock<IStubRegistry> _stubs = new();

    [Fact]
    public void EveryEndpointRequiresAnAdministrator()
    {
        var type = typeof(FederationStoreController);

        Assert.Equal(Policies.RequiresElevation, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("JellyMesh/Admin", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
        Assert.Equal("Store", type.GetMethod(nameof(FederationStoreController.GetStore))!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("Store/Retry", type.GetMethod(nameof(FederationStoreController.Retry))!.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal("Store/Recovery/Dismiss", type.GetMethod(nameof(FederationStoreController.DismissRecovery))!.GetCustomAttribute<HttpPostAttribute>()!.Template);
    }

    [Fact]
    public async Task AHealthyStoreGivesTheRestoreAndUntilWhenTheOldFilesAreKept()
    {
        var copy = _recovered.AddDays(-1);
        var kept = _recovered.AddDays(20);
        _database.Setup(database => database.Health).Returns(StoreHealth.Healthy);
        _database.Setup(database => database.Recovery).Returns(new StoreRecovery(_recovered, "jellymesh.db.damaged-20261118", copy));
        _database.Setup(database => database.MigratedKeptUntilAsync(It.IsAny<CancellationToken>())).ReturnsAsync(kept);

        var store = await Controller().GetStore(CancellationToken.None);

        Assert.Equal(StoreState.Healthy, store.State);
        Assert.Null(store.Reason);
        Assert.Equal(new StoreRecovery(_recovered, "jellymesh.db.damaged-20261118", copy), store.Recovery);
        Assert.Equal(kept, store.MigratedKeptUntil);
    }

    [Fact]
    public async Task ASuspendedStoreGivesItsReasonWithoutReadingTheOldFiles()
    {
        _database.Setup(database => database.Health).Returns(new StoreHealth(StoreState.Unavailable, "The database cannot be opened: disk full."));
        _database.Setup(database => database.MigratedKeptUntilAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new StoreSuspendedException("suspended"));

        var store = await Controller().GetStore(CancellationToken.None);

        Assert.Equal((StoreState.Unavailable, "The database cannot be opened: disk full."), (store.State, store.Reason));
        Assert.Null(store.MigratedKeptUntil);
    }

    // The read of the old files is what found the damage: the answer says so, not what the state was before it.
    [Fact]
    public async Task ADamageFoundReadingTheOldFilesShowsAsTheState()
    {
        var health = StoreHealth.Healthy;
        _database.Setup(database => database.Health).Returns(() => health);
        _database.Setup(database => database.MigratedKeptUntilAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            health = new StoreHealth(StoreState.Damaged, "The database is damaged: malformed.");
            return Task.FromException<DateTimeOffset?>(new StoreSuspendedException("damaged"));
        });

        var store = await Controller().GetStore(CancellationToken.None);

        Assert.Equal((StoreState.Damaged, "The database is damaged: malformed."), (store.State, store.Reason));
        Assert.Null(store.MigratedKeptUntil);
    }

    // A file or SQLite error reading the old files shows as the reason, cleaned as the other reasons, and fails nothing.
    [Theory]
    [MemberData(nameof(ReadErrors))]
    public async Task AnErrorReadingTheOldFilesShowsAsTheReason(Exception error, string shown)
    {
        _database.Setup(database => database.Health).Returns(StoreHealth.Healthy);
        _database.Setup(database => database.MigratedKeptUntilAsync(It.IsAny<CancellationToken>())).ThrowsAsync(error);

        var store = await Controller().GetStore(CancellationToken.None);

        Assert.Equal(StoreState.Healthy, store.State);
        Assert.Null(store.MigratedKeptUntil);
        Assert.Contains(shown, store.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', store.Reason!);
    }

    public static TheoryData<Exception, string> ReadErrors() => new()
    {
        { new IOException("/config/data/migrated-0.5\ncannot be read"), "/config/data/migrated-0.5cannot be read" },
        { new UnauthorizedAccessException("/config/data/migrated-0.5 is denied"), "/config/data/migrated-0.5 is denied" },
        { new SqliteException("SQLite Error 10: 'disk I/O error'.", 10), "disk I/O error" }
    };

    [Fact]
    public async Task RetryOpensTheStoreAgainAndAnswersWithItsStateAfter()
    {
        var health = new StoreHealth(StoreState.NewerSchema, "The database has schema version 99; this build knows up to 8.");
        _database.Setup(database => database.Health).Returns(() => health);
        _database.Setup(database => database.Retry()).Returns(() => health = StoreHealth.Healthy);

        var store = await Controller().Retry(CancellationToken.None);

        _database.Verify(database => database.Retry(), Times.Once);
        Assert.Equal(StoreState.Healthy, store.State);
    }

    [Fact]
    public async Task RetryReadsTheStubRegistryAgainBeforeItAnswers()
    {
        var order = new List<string>();
        var read = new TaskCompletionSource();
        _database.Setup(database => database.Health).Returns(StoreHealth.Healthy);
        _database.Setup(database => database.Retry()).Callback(() => order.Add("retry")).Returns(StoreHealth.Healthy);
        _stubs.Setup(stubs => stubs.ReloadAsync(It.IsAny<CancellationToken>())).Callback(() => order.Add("reload")).Returns(read.Task);

        var answer = Controller().Retry(CancellationToken.None);

        Assert.False(answer.IsCompleted);
        read.SetResult();
        await answer;
        Assert.Equal(["retry", "reload"], order);
    }

    [Fact]
    public async Task RetryThatLeavesTheStoreSuspendedReadsNoStubRegistry()
    {
        var suspended = new StoreHealth(StoreState.Unavailable, "The database cannot be opened: disk full.");
        _database.Setup(database => database.Health).Returns(suspended);
        _database.Setup(database => database.Retry()).Returns(suspended);

        await Controller().Retry(CancellationToken.None);

        _stubs.Verify(stubs => stubs.ReloadAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RetryAnswersWhenTheReadOfTheStubRegistryTakesTooLong()
    {
        _database.Setup(database => database.Health).Returns(StoreHealth.Healthy);
        _database.Setup(database => database.Retry()).Returns(StoreHealth.Healthy);
        _stubs.Setup(stubs => stubs.ReloadAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellation) => Task.Delay(Timeout.InfiniteTimeSpan, cancellation));

        var store = await Controller(TimeSpan.FromMilliseconds(50)).Retry(CancellationToken.None);

        Assert.Equal(StoreState.Healthy, store.State);
    }

    [Fact]
    public async Task RetryOfACallerThatLeftIsCancelled()
    {
        using var left = new CancellationTokenSource();
        _database.Setup(database => database.Health).Returns(StoreHealth.Healthy);
        _database.Setup(database => database.Retry()).Returns(StoreHealth.Healthy);
        _stubs.Setup(stubs => stubs.ReloadAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken cancellation) => Task.Delay(Timeout.InfiniteTimeSpan, cancellation));

        var answer = Controller().Retry(left.Token);
        await left.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);
    }

    [Fact]
    public void DismissingTheRestoreForgetsIt()
    {
        var result = Controller().DismissRecovery();

        Assert.IsType<NoContentResult>(result);
        _database.Verify(database => database.DismissRecovery(), Times.Once);
    }

    private FederationStoreController Controller(TimeSpan? reloadLimit = null) => new(_database.Object, _stubs.Object) { ReloadLimit = reloadLimit ?? TimeSpan.FromSeconds(30) };
}
