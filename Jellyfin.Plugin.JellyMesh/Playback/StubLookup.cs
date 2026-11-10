using System;
using System.Collections.Generic;
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
    /// <summary>
    /// The field of the legacy <c>params</c> query string that stock reads as the version, over <c>mediaSourceId</c>.
    /// </summary>
    public const int LegacyMediaSourceField = 2;

    private static readonly string[] _namingArguments = ["itemId", "routeItemId", "videoId", "mediaSourceId", "routeMediaSourceId"];

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
    /// Finds every item a stock action names, as its entity or as the version it picks.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <returns>The item identifiers, without repeats.</returns>
    public static IReadOnlyCollection<Guid> NamedItemIdsOf(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ids = new HashSet<Guid>();
        foreach (var name in _namingArguments)
        {
            if (context.ActionArguments.TryGetValue(name, out var value)
                && (value is Guid id || (value is string text && Guid.TryParse(text, out id)))
                && !id.Equals(Guid.Empty))
            {
                ids.Add(id);
            }
        }

        if (Guid.TryParse(LegacyParamOf(context, LegacyMediaSourceField), out var version) && !version.Equals(Guid.Empty))
        {
            ids.Add(version);
        }

        return ids;
    }

    /// <summary>
    /// Reads one field of the legacy <c>params</c> query string, which stock splits on ';' and applies first.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <param name="field">The field, from zero.</param>
    /// <returns>The field, null when it is missing or blank, since stock skips it then.</returns>
    public static string? LegacyParamOf(ActionExecutingContext context, int field)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.ActionArguments.TryGetValue("params", out var value) || value is not string text)
        {
            return null;
        }

        var fields = text.Split(';');
        return field < fields.Length && !string.IsNullOrWhiteSpace(fields[field]) ? fields[field] : null;
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
