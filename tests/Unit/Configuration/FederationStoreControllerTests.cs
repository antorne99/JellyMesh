using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>The state of the plugin database on the page: its reason and Retry, the restore banner and the old files (rows 427, 466, 469).</summary>
public sealed class FederationStoreControllerTests
{
    private static readonly DateTimeOffset _recovered = new(2026, 11, 18, 3, 5, 0, TimeSpan.Zero);

    private readonly Mock<IPluginDatabase> _database = new();

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
    public void DismissingTheRestoreForgetsIt()
    {
        var result = Controller().DismissRecovery();

        Assert.IsType<NoContentResult>(result);
        _database.Verify(database => database.DismissRecovery(), Times.Once);
    }

    private FederationStoreController Controller() => new(_database.Object);
}
