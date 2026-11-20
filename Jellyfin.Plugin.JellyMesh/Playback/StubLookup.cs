using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
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
    /// Tells whether a user reaches an item: Jellyfin shows it to them and, unless they are an admin, a stub or a version
    /// sits in a library, since Jellyfin shows an item with no library to everyone (D17).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>True when the user may reach it.</returns>
    public bool Sees(User user, Guid itemId)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (_libraryManager.GetItemById<BaseItem>(itemId, user) is not { } item)
        {
            return false;
        }

        return user.HasPermission(PermissionKind.IsAdministrator)
            || (FindFile(itemId) is null && !HasVersions(itemId))
            || _libraryManager.GetCollectionFolders(item).Count > 0;
    }

    /// <summary>
    /// Tells whether an item is a stub by the URL of its <c>.strm</c>, even one the registry no longer knows,
    /// or by a <c>.strm</c> path in the federated folder before Jellyfin has read it.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for a stub.</returns>
    public bool IsStub(Guid itemId)
    {
        return _libraryManager.GetItemById(itemId) switch
        {
            Video video => PlaybackRoutes.TryGetStubId(video.ShortcutPath, out _) || IsStrmInFolder(video.Path),
            Audio audio => IsStrmInFolder(audio.Path),
            var item => IsPlaceholder(item)
        };
    }

    /// <summary>
    /// Tells whether an item is an audio stub: an audio <c>.strm</c> in the federated folder, which has no plugin URL.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for an audio stub.</returns>
    public bool IsAudioStub(Guid itemId) => _libraryManager.GetItemById(itemId) is Audio audio && IsStrmInFolder(audio.Path);

    /// <summary>
    /// Tells whether an item is a placeholder: a book of the federated folder, which has no URL and is a stub by its path.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for a placeholder, even one the registry does not know.</returns>
    public bool IsPlaceholder(Guid itemId) => IsPlaceholder(_libraryManager.GetItemById(itemId));

    /// <summary>
    /// Finds the stub of an item.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The stub, null for anything else.</returns>
    public StubEntry? Find(Guid itemId)
    {
        return _libraryManager.GetItemById(itemId) switch
        {
            Video video => PlaybackRoutes.TryGetStubId(video.ShortcutPath, out var stubId) ? _stubs.Find(stubId) : null,
            var item when PlaceholderPath(item) is { } path => _stubs.FindByPath(path),
            _ => null
        };
    }

    /// <summary>
    /// Finds the stub a playback of an item reaches: a video stub by its URL, or a book placeholder or audio stub by its
    /// path.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The stub, null for anything else.</returns>
    public StubEntry? FindPlayable(Guid itemId) => Find(itemId) ?? (IsAudioStub(itemId) ? FindFile(itemId) : null);

    /// <summary>
    /// Finds the stub of an item for its file and its visibility: a video stub, or an audio stub or book placeholder by its
    /// path.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The stub, null for anything else.</returns>
    public StubEntry? FindFile(Guid itemId)
    {
        return _libraryManager.GetItemById(itemId) switch
        {
            Video video when PlaybackRoutes.TryGetStubId(video.ShortcutPath, out var stubId) => _stubs.Find(stubId),
            Audio audio when PathInFolder(audio.Path) is { } path => _stubs.FindByPath(path),
            var item when PlaceholderPath(item) is { } path => _stubs.FindByPath(path),
            _ => null
        };
    }

    /// <summary>
    /// Finds the items Jellyfin links with an item as versions: it climbs through primaries and owners, as the linker does,
    /// and takes each one and their alternates.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The linked items with the item itself, empty when it is not a video.</returns>
    public IReadOnlyDictionary<Guid, Video> LinkedVersionsOf(Guid itemId)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video)
        {
            return new Dictionary<Guid, Video>();
        }

        var members = new Dictionary<Guid, Video> { [itemId] = video };
        if (NonEmpty(video.PrimaryVersionId) is null && NonEmpty(video.OwnerId) is null && video.LinkedAlternateVersions.Length == 0)
        {
            return members;
        }

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

        return members;
    }

    // A link only between local films stays stock.
    private bool IsLinkedToStub(Guid itemId)
    {
        var members = LinkedVersionsOf(itemId);
        if (members.Count < 2)
        {
            return false;
        }

        var root = _store.ReadOr(config => config.FederatedFolderPath, null);
        foreach (var member in members.Values)
        {
            if ((root is null ? IsStrm(member.Path) : FederatedPaths.IsInside(root, member.Path))
                || (PlaybackRoutes.TryGetStubId(member.ShortcutPath, out var stubId) && _stubs.Find(stubId) is not null))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPlaceholder(BaseItem? item) => PlaceholderPath(item) is not null;

    // The path of a book in the federated folder, also through an alias of it, spelled under the folder; else null.
    private string? PlaceholderPath(BaseItem? item)
    {
        if (item is not Book { Path: { Length: > 0 } path } || _store.ReadOr(config => config.FederatedFolderPath, null) is not { Length: > 0 } root)
        {
            return null;
        }

        return FederatedPaths.IsInside(root, path) ? path : RealPath.Rebase(root, path);
    }

    private static bool IsStrm(string? path) => path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true;

    // A .strm in the federated folder; while the folder is not known, any .strm, since stock would send its signed URL.
    private bool IsStrmInFolder(string? path) => PathInFolder(path) is not null;

    // The path of a .strm in the federated folder, also through an alias of it, spelled under the folder; else null.
    private string? PathInFolder(string? path)
    {
        if (!IsStrm(path))
        {
            return null;
        }

        if (_store.ReadOr(config => config.FederatedFolderPath, null) is not { } root || FederatedPaths.IsInside(root, path))
        {
            return path;
        }

        return RealPath.Rebase(root, path!);
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
