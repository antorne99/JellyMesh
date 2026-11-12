using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Answers 404 to a user who is not an administrator and names, as <c>ParentId</c>, the old id of a library the plugin
/// renamed: stock still lists it under that id until a restart, past the user's blocked list (§10 row 86).
/// </summary>
public sealed class RetiredLibraryFilter : IAsyncResourceFilter
{
    private const string ParentParameter = "parentId";

    private readonly RetiredLibraryIds _retired;
    private readonly IAuthorizationContext _authorization;

    /// <summary>
    /// Initializes a new instance of the <see cref="RetiredLibraryFilter"/> class.
    /// </summary>
    /// <param name="retired">The retired library ids.</param>
    /// <param name="authorization">The authorization context.</param>
    public RetiredLibraryFilter(RetiredLibraryIds retired, IAuthorizationContext authorization)
    {
        _retired = retired;
        _authorization = authorization;
    }

    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (!_retired.IsEmpty && NamesRetired(context.HttpContext.Request.Query))
        {
            var user = (await _authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false)).User;
            if (user is not null && !user.HasPermission(PermissionKind.IsAdministrator))
            {
                context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
                return;
            }
        }

        await next().ConfigureAwait(false);
    }

    // Stock binds the parameter whatever its case, so every key that matches is read.
    private bool NamesRetired(IQueryCollection query) => query
        .Where(pair => string.Equals(pair.Key, ParentParameter, StringComparison.OrdinalIgnoreCase))
        .SelectMany(pair => pair.Value)
        .Any(value => Guid.TryParse(value, out var id) && _retired.Contains(id));
}
