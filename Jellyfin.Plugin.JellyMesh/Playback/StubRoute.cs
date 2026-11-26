using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// A stock route the guard answers itself when it names a stub: the stubs it covers, the access it wants and its answer.
/// </summary>
/// <param name="Controller">The stock controller.</param>
/// <param name="Action">The stock action, null for every action of the controller.</param>
/// <param name="Covers">The stubs it covers.</param>
/// <param name="Access">The access it wants.</param>
/// <param name="Answer">The answer, given once the access is granted, with the stubs the route names.</param>
internal sealed record StubRoute(
    string Controller,
    string? Action,
    StubCover Covers,
    StubAccess Access,
    Func<StockRouteGuardFilter, ActionExecutingContext, AuthorizationInfo, List<Guid>, Task<IActionResult>> Answer);
