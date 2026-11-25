using System;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Answers an exception of an administrator endpoint with its whole story, which it also logs.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AdminErrorsAttribute : ExceptionFilterAttribute
{
    /// <inheritdoc />
    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.HttpContext.Request;
        if (context.ExceptionHandled || (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested))
        {
            return;
        }

        var detail = ErrorText.Of(context.Exception);
        var services = context.HttpContext.RequestServices;
        services.GetService<ILogger<AdminErrorsAttribute>>()?.LogError(
            "{Method} {Path} failed: {Error}{Detail:l}",
            LogText.Clean(request.Method),
            LogText.Clean(request.Path.Value ?? string.Empty),
            context.Exception.GetType().Name,
            services.GetService<ErrorLogLimits>()?.Local.Of(detail) ?? detail);
        context.Result = new ObjectResult(new FailureResponse(RemoteFailure.None, FederationAdminController.FailureMessage(RemoteFailure.None)) { Detail = detail.TrimStart() })
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
        context.ExceptionHandled = true;
    }
}
