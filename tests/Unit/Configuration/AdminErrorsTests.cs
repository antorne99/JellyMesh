using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Provider;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

public class AdminErrorsTests
{
    private readonly CapturingLogger<AdminErrorsAttribute> _logger = new();

    [Fact]
    public void AnExceptionIsAnsweredAndLoggedWhole()
    {
        var context = ContextOf(Thrown(new IOException("disk full", new InvalidOperationException("inner cause"))));

        new AdminErrorsAttribute().OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        var detail = Assert.IsType<FailureResponse>(result.Value).Detail!;
        Assert.StartsWith("System.IO.IOException: disk full", detail, StringComparison.Ordinal);
        Assert.Contains("inner cause", detail, StringComparison.Ordinal);
        Assert.Contains("   at " + typeof(AdminErrorsTests).FullName, detail, StringComparison.Ordinal);
        var line = Assert.Single(_logger.Lines);
        Assert.Contains("POST /JellyMesh/Admin/Sync failed: IOException", line, StringComparison.Ordinal);
        Assert.Contains("inner cause", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestTheClientLeftIsNotAnError()
    {
        var context = ContextOf(new OperationCanceledException());
        context.HttpContext.RequestAborted = new CancellationToken(canceled: true);

        new AdminErrorsAttribute().OnException(context);

        Assert.False(context.ExceptionHandled);
        Assert.Empty(_logger.Lines);
    }

    [Fact]
    public void EveryControllerThatTellsWholeErrorsIsForAdministrators()
    {
        var controllers = typeof(AdminErrorsAttribute).Assembly.GetTypes().Where(type => type.GetCustomAttribute<AdminErrorsAttribute>() is not null).ToList();

        Assert.Equal(4, controllers.Count);
        Assert.Contains(typeof(PeerAdminController), controllers);
        Assert.All(controllers, type => Assert.Equal(Policies.RequiresElevation, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy));
    }

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception thrown)
        {
            return thrown;
        }
    }

    private ExceptionContext ContextOf(Exception exception)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<ILogger<AdminErrorsAttribute>>(_logger).BuildServiceProvider()
        };
        http.Request.Method = "POST";
        http.Request.Path = "/JellyMesh/Admin/Sync";
        return new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), []) { Exception = exception };
    }
}
