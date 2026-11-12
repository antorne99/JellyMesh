using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Finds the stub behind a request on a stock route.
/// </summary>
public sealed class StubLookup
{
    /// <summary>
    /// The field of the legacy <c>params</c> query string that stock reads as the device, over <c>deviceId</c>.
    /// </summary>
    public const int LegacyDeviceField = 1;

    /// <summary>
    /// The field of the legacy <c>params</c> query string that stock reads as the version, over <c>mediaSourceId</c>.
    /// </summary>
    public const int LegacyMediaSourceField = 2;

    /// <summary>
    /// The field of the legacy <c>params</c> query string that stock reads as the play session, over <c>playSessionId</c>.
    /// </summary>
    public const int LegacyPlaySessionField = 20;

    /// <summary>
    /// The field of the legacy <c>params</c> query string that stock reads as the live stream, over <c>liveStreamId</c>.
    /// </summary>
    public const int LegacyLiveStreamField = 22;

    // How many levels above the item the version guard climbs: a file version, its owner, then that film's primary.
    private const int MaxClimb = 4;

    private static readonly string[] _namingArguments = ["itemId", "routeItemId", "videoId", "mediaSourceId", "routeMediaSourceId"];

    private static readonly string[] _reportArguments = ["playbackStartInfo", "playbackProgressInfo", "playbackStopInfo"];

    private readonly ILibraryManager _libraryManager;
    private readonly IStubRegistry _stubs;
    private readonly IPluginConfigurationStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubLookup"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="stubs">The stubs.</param>
    /// <param name="store">The configuration, for the federated folder.</param>
    public StubLookup(ILibraryManager libraryManager, IStubRegistry stubs, IPluginConfigurationStore store)
    {
        _libraryManager = libraryManager;
        _stubs = stubs;
        _store = store;
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
    /// Finds every item a stock action names, as its entity or as the version it picks, also in a Playstate report.
    /// </summary>
    /// <param name="context">The action.</param>
    /// <returns>The item identifiers, without repeats.</returns>
    public static IReadOnlyCollection<Guid> NamedItemIdsOf(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var ids = new HashSet<Guid>();
        foreach (var name in _namingArguments)
        {
            if (context.ActionArguments.TryGetValue(name, out var value))
            {
                Add(ids, value);
            }
        }

        Add(ids, LegacyParamOf(context, LegacyMediaSourceField));
        foreach (var name in _reportArguments)
        {
            context.ActionArguments.TryGetValue(name, out var value);
            if (value is PlaybackStopInfo stop)
            {
                Add(ids, stop.ItemId);
                Add(ids, stop.MediaSourceId);
            }
            else if (value is PlaybackProgressInfo progress)
            {
                Add(ids, progress.ItemId);
                Add(ids, progress.MediaSourceId);
            }
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

        return LegacyParamOf(context.ActionArguments.TryGetValue("params", out var value) ? (value as string) : null, field);
    }

    /// <summary>
    /// Reads one field of a legacy <c>params</c> value, which stock splits on ';' and applies first.
    /// </summary>
    /// <param name="parameters">The value, as stock binds it.</param>
    /// <param name="field">The field, from zero.</param>
    /// <returns>The field, null when it is missing or blank, since stock skips it then.</returns>
    public static string? LegacyParamOf(string? parameters, int field)
    {
        if (parameters is null)
        {
            return null;
        }

        var fields = parameters.Split(';');
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
    /// Tells whether an item has linked versions: a stub or a local copy in a group of the registry with another member,
    /// or an item Jellyfin links to a stub, since its links outlive a group until the nightly run (Q168, Q169).
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for a member of such a group or link.</returns>
    public bool HasVersions(Guid itemId)
    {
        var group = Find(itemId) is { } stub ? _stubs.FindGroup(stub.Id) : _stubs.FindGroupOfLocal(itemId);
        return (group is not null && group.StubIds.Count + group.LocalItemIds.Count > 1) || IsLinkedToStub(itemId);
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

    // Climbs from the item through primaries and owners, as the linker does, and looks at each and their alternates.
    // A link only between local films stays stock.
    private bool IsLinkedToStub(Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video)
        {
            return false;
        }

        if (NonEmpty(video.PrimaryVersionId) is null && NonEmpty(video.OwnerId) is null && video.LinkedAlternateVersions.Length == 0)
        {
            return false;
        }

        var members = new Dictionary<Guid, Video> { [itemId] = video };
        var climbed = new HashSet<Guid>();
        var level = new List<Video> { video };
        for (var depth = 0; depth <= MaxClimb && level.Count > 0; depth++)
        {
            var next = new List<Video>();
            foreach (var node in level.Where(node => climbed.Add(node.Id)))
            {
                members[node.Id] = node;
                AddAlternates(members, node);
                foreach (var aboveId in new[] { NonEmpty(node.PrimaryVersionId), NonEmpty(node.OwnerId) }.OfType<Guid>())
                {
                    if (!climbed.Contains(aboveId) && (members.GetValueOrDefault(aboveId) ?? _libraryManager.GetItemById(aboveId) as Video) is { } above)
                    {
                        next.Add(above);
                    }
                }
            }

            level = next;
        }

        if (members.Count < 2)
        {
            return false;
        }

        var root = _store.Read(config => config.FederatedFolderPath);
        foreach (var member in members.Values)
        {
            if (FederatedPaths.IsInside(root, member.Path) || (PlaybackRoutes.TryGetStubId(member.ShortcutPath, out var stubId) && _stubs.Find(stubId) is not null))
            {
                return true;
            }
        }

        return false;
    }

    private void AddAlternates(Dictionary<Guid, Video> members, Video video)
    {
        foreach (var child in video.LinkedAlternateVersions)
        {
            if (child.ItemId is { } id && !id.Equals(Guid.Empty) && !members.ContainsKey(id) && _libraryManager.GetItemById(id) is Video alternate)
            {
                members[id] = alternate;
            }
        }
    }

    private static Guid? NonEmpty(Guid? id) => id is { } value && !value.Equals(Guid.Empty) ? value : null;

    // Adds an item named as a Guid or as text, as stock binds a version.
    private static void Add(HashSet<Guid> ids, object? value)
    {
        if ((value is Guid id || (value is string text && Guid.TryParse(text, out id))) && !id.Equals(Guid.Empty))
        {
            ids.Add(id);
        }
    }
}
