using System;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Answers 503 on the stock routes to a user who is not an administrator until the plugin database has loaded once: until
/// then no peer is known, so none can be told apart from a local user (D32). Once loaded the configuration stays in memory.
/// </summary>
public sealed class StoreNotLoadedFilter : IAsyncResourceFilter
{
    /// <summary>The fixed answer, which says nothing of why.</summary>
    internal const string Body = "Service unavailable.";

    private readonly IPluginConfigurationStore _store;
    private readonly IAuthorizationContext _authorization;
    private volatile bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="StoreNotLoadedFilter"/> class.
    /// </summary>
    /// <param name="store">The plugin configuration store.</param>
    /// <param name="authorization">The authorization context.</param>
    public StoreNotLoadedFilter(IPluginConfigurationStore store, IAuthorizationContext authorization)
    {
        _store = store;
        _authorization = authorization;
    }

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!IsPluginRoute(context.ActionDescriptor) && !HasLoaded())
        {
            var user = (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
            if (user is not null && !user.HasPermission(PermissionKind.IsAdministrator))
            {
                context.Result = new ContentResult
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable,
                    Content = Body,
                    ContentType = "text/plain"
                };
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    // The plugin's own controllers answer for themselves while the database cannot be read.
    private static bool IsPluginRoute(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor action)
        => action is ControllerActionDescriptor controller && controller.ControllerTypeInfo.Assembly == typeof(StoreNotLoadedFilter).Assembly;

    // Tries to read the store until it has loaded once; after that the store keeps it, so it is not asked again.
    private bool HasLoaded() => _loaded || (_loaded = _store.ReadOr(_ => true, false));
}
