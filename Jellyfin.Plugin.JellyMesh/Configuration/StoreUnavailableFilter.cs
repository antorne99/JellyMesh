using System;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Answers 503 for a request that needed the JellyMesh database while it cannot be used, in place of a 500. Only
/// JellyMesh code throws <see cref="StoreSuspendedException"/>, so a stock request that never reaches it is left alone.
/// </summary>
public sealed class StoreUnavailableFilter : IExceptionFilter
{
    /// <summary>The message of the answer, translated on the configuration page.</summary>
    internal const string Message = "The JellyMesh database cannot be used now, so JellyMesh is suspended until it can.";

    /// <summary>
    /// Builds the answer for a request that needs the database.
    /// </summary>
    /// <returns>A 503 with the message.</returns>
    public static ObjectResult Unavailable()
        => new(new FailureResponse(RemoteFailure.None, Message)) { StatusCode = StatusCodes.Status503ServiceUnavailable };

    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Exception is StoreSuspendedException)
        {
            context.Result = Unavailable();
            context.ExceptionHandled = true;
        }
    }
}
