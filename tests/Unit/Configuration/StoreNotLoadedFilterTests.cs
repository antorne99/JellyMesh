using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Tests.Provider;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>D32: until the plugin database has loaded once, non-admin users get 503 on the stock routes.</summary>
public class StoreNotLoadedFilterTests
{
    private readonly InMemoryConfigurationStore _store = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private User? _caller = new("mario", "auth", "reset");

    public StoreNotLoadedFilterTests()
    {
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(() => new AuthorizationInfo { User = _caller });
    }

    public static TheoryData<string, string> StockRoutes => new()
    {
        { "Items", "GetItems" },
        { "MediaInfo", "GetPostedPlaybackInfo" },
        { "Videos", "GetVideoStream" },
        { "Image", "GetItemImage" },
        { "Library", "GetDownload" }
    };

    private async Task<(ResourceExecutingContext Context, bool Ran)> RunAsync(ControllerActionDescriptor action, IPluginConfigurationStore? store = null)
    {
        var context = new ResourceExecutingContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), action),
            new List<IFilterMetadata>(),
            new List<IValueProviderFactory>());
        var ran = false;
        await new StoreNotLoadedFilter(store ?? _store, _authorization.Object).OnResourceExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        });
        return (context, ran);
    }

    [Theory]
    [MemberData(nameof(StockRoutes))]
    public async Task BeforeTheFirstLoadAUserWhoIsNoAdminGetsUnavailable(string controller, string action)
    {
        _store.Unavailable = true;

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action));

        Assert.False(ran);
        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(StoreNotLoadedFilter.Body, result.Content);
    }

    [Fact]
    public void TheAnswerTellsNothingOfWhy()
    {
        Assert.DoesNotContain("JellyMesh", StoreNotLoadedFilter.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("database", StoreNotLoadedFilter.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(StockRoutes))]
    public async Task BeforeTheFirstLoadAnAdminPasses(string controller, string action)
    {
        _caller!.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));
        _store.Unavailable = true;

        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action));

        Assert.True(ran);
        Assert.Null(context.Result);
    }

    // Stock answers an anonymous request for everyone alike, or refuses it by itself.
    [Fact]
    public async Task BeforeTheFirstLoadAnAnonymousRequestPassesAsOnStock()
    {
        _caller = null;
        _store.Unavailable = true;

        var (_, ran) = await RunAsync(ProviderFixture.Stock("Image", "GetItemImage"));

        Assert.True(ran);
    }

    // The plugin's own routes answer for themselves: the store filter gives 503, the .strm route serves the server.
    [Fact]
    public async Task BeforeTheFirstLoadThePluginRoutesAreLeftToThemselves()
    {
        _store.Unavailable = true;

        var (_, ran) = await RunAsync(ProviderFixture.Plugin<ProviderController>("GetLibraries"));

        Assert.True(ran);
    }

    [Theory]
    [MemberData(nameof(StockRoutes))]
    public async Task OnceLoadedAUserWhoIsNoAdminPassesWithoutBeingLookedUp(string controller, string action)
    {
        var (context, ran) = await RunAsync(ProviderFixture.Stock(controller, action));

        Assert.True(ran);
        Assert.Null(context.Result);
        _authorization.Verify(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()), Times.Never);
    }

    [Fact]
    public void ThePluginAppliesItToEveryRoute()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());

        var filters = services.BuildServiceProvider().GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
        Assert.Contains(filters, filter => filter is ServiceFilterAttribute { ServiceType: var type } && type == typeof(StoreNotLoadedFilter));
        Assert.Contains(services, service => service.ServiceType == typeof(StoreNotLoadedFilter));
    }
}
