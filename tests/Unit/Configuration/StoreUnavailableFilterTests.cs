using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Storage;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>What a request that needs the JellyMesh settings gets while the plugin database cannot be used.</summary>
public class StoreUnavailableFilterTests
{
    [Fact]
    public void ARequestThatNeedsTheSettingsWhileTheyCannotBeReadIsUnavailable()
    {
        var context = Context(new StoreSuspendedException("never read"));

        new StoreUnavailableFilter().OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(StoreUnavailableFilter.Message, Assert.IsType<FailureResponse>(result.Value).Message);
        Assert.True(context.ExceptionHandled);
    }

    [Fact]
    public void AnyOtherFailureIsLeftToJellyfin()
    {
        var context = Context(new InvalidOperationException("other"));

        new StoreUnavailableFilter().OnException(context);

        Assert.Null(context.Result);
        Assert.False(context.ExceptionHandled);
    }

    [Fact]
    public void ThePluginAppliesItToEveryController()
    {
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());

        var filters = services.BuildServiceProvider().GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
        Assert.Contains(filters, filter => filter is TypeFilterAttribute { ImplementationType: var type } && type == typeof(StoreUnavailableFilter));
    }

    private static ExceptionContext Context(Exception exception)
        => new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()) { Exception = exception };
}
