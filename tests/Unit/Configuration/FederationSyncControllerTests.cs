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

    private FederationSyncController Controller() => new(_fixture.Sync, Mock.Of<IFederationResolver>());

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
}
