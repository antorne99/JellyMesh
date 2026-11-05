using System;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Finds the stub behind a request on a stock route.
/// </summary>
public sealed class StubLookup
{
    private readonly ILibraryManager _libraryManager;
    private readonly IStubRegistry _stubs;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubLookup"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="stubs">The stubs.</param>
    public StubLookup(ILibraryManager libraryManager, IStubRegistry stubs)
    {
        _libraryManager = libraryManager;
        _stubs = stubs;
    }

    /// <summary>
    /// Finds the item a stock action works on.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <returns>The item identifier, null if the action names none.</returns>
    public static Guid? ItemIdOf(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var name in new[] { "itemId", "routeItemId" })
        {
            if (context.ActionArguments.TryGetValue(name, out var value) && value is Guid id && !id.Equals(Guid.Empty))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the controller and action names of a stock action.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <returns>The names, empty for anything else.</returns>
    public static (string Controller, string Action) ActionOf(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.ActionDescriptor is ControllerActionDescriptor action
            ? (action.ControllerName, action.ActionName)
            : (string.Empty, string.Empty);
    }

    /// <summary>
    /// Finds the stub of an item.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The stub, null for anything else.</returns>
    public StubEntry? Find(Guid itemId)
    {
        return _libraryManager.GetItemById(itemId) is Video video && PlaybackRoutes.TryGetStubId(video.ShortcutPath, out var stubId)
            ? _stubs.Find(stubId)
            : null;
    }
}
