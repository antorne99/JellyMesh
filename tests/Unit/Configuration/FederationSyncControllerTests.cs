using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

public sealed class FederationSyncControllerTests : IDisposable
{
    private readonly SyncFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private FederationSyncController Controller(ISyncHistory? history = null) => new(_fixture.Sync, Mock.Of<IFederationResolver>(), history ?? Mock.Of<ISyncHistory>());

    [Fact]
    public void EveryEndpointRequiresAnAdministrator()
    {
        var type = typeof(FederationSyncController);

        Assert.Equal(Policies.RequiresElevation, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("JellyMesh/Admin", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
    }

    [Fact]
    public async Task StartingASyncAnswersAccepted()
    {
        await _fixture.AddServerAsync("A");

        var result = Controller().StartSync();

        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsType<AcceptedResult>(result.Result).StatusCode);
    }

    [Fact]
    public void WhileSuspendedItAnswersConflict()
    {
        _fixture.KeySource.Key = null;

        var result = Assert.IsType<ConflictObjectResult>(Controller().StartSync().Result);

        Assert.Equal(RemoteFailure.Suspended, ((FailureResponse)result.Value!).Failure);
    }

    [Fact]
    public void TheHistoryIsGivenNewestFirst_WithItsDamage()
    {
        var newer = new SyncRun(SyncFixture.Start.AddMinutes(15), 10, SyncRunKind.Incremental, SyncRunOutcome.Succeeded, []);
        var older = new SyncRun(SyncFixture.Start, 20, SyncRunKind.Reconciliation, SyncRunOutcome.Failed, []);
        var history = new Mock<ISyncHistory>();
        history.SetupGet(item => item.Runs).Returns([newer, older]);
        history.SetupGet(item => item.IsDamaged).Returns(true);

        var view = Controller(history.Object).GetHistory().Value!;

        Assert.Equal([newer, older], view.Runs);
        Assert.True(view.IsDamaged);
    }
}
