using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Library;

public class RetiredLibraryFilterTests
{
    private static readonly Guid _old = Guid.Parse("0b6b5d1c-8a52-4f0e-9a51-2f4f0d6c9e11");
    private static readonly Guid _other = Guid.Parse("7c1e0e7f-2d0b-4a77-8f02-1a2b3c4d5e6f");

    private readonly RetiredLibraryIds _retired = new();
    private readonly Mock<IAuthorizationContext> _authorization = new();
    private User? _caller = new("mario", "auth", "reset");

    public RetiredLibraryFilterTests()
    {
        _authorization.Setup(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()))
            .ReturnsAsync(() => new AuthorizationInfo { User = _caller });
    }

    private async Task<(ResourceExecutingContext Context, bool Ran)> RunAsync(string query, string? routeKey = null, string? routeValue = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "GET";
        http.Request.QueryString = new QueryString(query);
        var action = new ControllerActionDescriptor { ControllerName = "Items", ActionName = "GetItems" };
        var route = new RouteData();
        if (routeKey is not null)
        {
            route.Values[routeKey] = routeValue;
        }

        var context = new ResourceExecutingContext(new ActionContext(http, route, action), new List<IFilterMetadata>(), new List<IValueProviderFactory>());
        var ran = false;
        await new RetiredLibraryFilter(_retired, _authorization.Object).OnResourceExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ResourceExecutedContext(context, new List<IFilterMetadata>()));
        });
        return (context, ran);
    }

    [Theory]
    [InlineData("?ParentId={0:N}")]
    [InlineData("?parentid={0:D}")]
    [InlineData("?Recursive=true&PARENTID={0:N}")]
    public async Task A_user_who_names_a_retired_library_as_parent_gets_404(string query)
    {
        _retired.Retire(_old);

        var (context, ran) = await RunAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, query, _old));

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, (context.Result as StatusCodeResult)?.StatusCode);
    }

    [Theory]
    [InlineData("itemId", "{0:D}")]
    [InlineData("itemId", "{0:N}")]
    [InlineData("ItemId", "{0:D}")]
    public async Task A_user_who_opens_a_retired_library_by_its_id_gets_404(string key, string value)
    {
        _retired.Retire(_old);

        var (context, ran) = await RunAsync(string.Empty, key, string.Format(System.Globalization.CultureInfo.InvariantCulture, value, _old));

        Assert.False(ran);
        Assert.Equal(StatusCodes.Status404NotFound, (context.Result as StatusCodeResult)?.StatusCode);
    }

    [Fact]
    public async Task An_administrator_still_opens_the_old_id()
    {
        _retired.Retire(_old);
        _caller!.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));

        Assert.True((await RunAsync(string.Empty, "itemId", _old.ToString("D"))).Ran);
    }

    [Fact]
    public async Task Other_items_and_other_route_values_pass()
    {
        _retired.Retire(_old);

        Assert.True((await RunAsync(string.Empty, "itemId", _other.ToString("D"))).Ran);
        Assert.True((await RunAsync(string.Empty, "userId", _old.ToString("D"))).Ran);
        Assert.True((await RunAsync(string.Empty, "itemId", "not-a-guid")).Ran);
    }

    [Fact]
    public async Task An_administrator_still_reaches_the_old_id()
    {
        _retired.Retire(_old);
        _caller!.Permissions.Add(new Permission(PermissionKind.IsAdministrator, true));

        var (_, ran) = await RunAsync($"?ParentId={_old:N}");

        Assert.True(ran);
    }

    [Fact]
    public async Task Other_parents_and_requests_without_a_parent_pass()
    {
        _retired.Retire(_old);

        Assert.True((await RunAsync($"?ParentId={_other:N}")).Ran);
        Assert.True((await RunAsync("?Recursive=true")).Ran);
        Assert.True((await RunAsync("?ParentId=not-a-guid")).Ran);
    }

    [Fact]
    public async Task With_nothing_retired_the_caller_is_not_even_looked_up()
    {
        Assert.True((await RunAsync($"?ParentId={_old:N}")).Ran);

        _authorization.Verify(context => context.GetAuthorizationInfo(It.IsAny<HttpContext>()), Times.Never);
    }

    [Fact]
    public async Task An_id_that_comes_back_as_a_live_library_is_no_longer_retired()
    {
        _retired.Retire(_old);
        _retired.Restore(_old);

        Assert.True((await RunAsync($"?ParentId={_old:N}")).Ran);
    }

    [Fact]
    public async Task An_anonymous_request_passes_to_stock_which_refuses_it()
    {
        _retired.Retire(_old);
        _caller = null;

        Assert.True((await RunAsync($"?ParentId={_old:N}")).Ran);
    }
}
