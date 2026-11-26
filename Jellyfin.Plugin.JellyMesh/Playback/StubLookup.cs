using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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

    // The most a .strm of the plugin holds: its URL is about 140 bytes.
    private const int MaxStrmBytes = 1024;

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
    /// Tells whether a user reaches an item: Jellyfin shows it to them and, unless they are an admin, a stub, also one the
    /// registry no longer knows (row 514), or a version sits in a library, as Jellyfin shows one with none to all (D17).
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
            || (!IsStub(itemId) && FindWithThumbnail(itemId) is null && Resolve(itemId, out var isUnknown) is null && !isUnknown && !HasVersions(itemId))
            || _libraryManager.GetCollectionFolders(item).Count > 0;
    }

    /// <summary>
    /// Tells whether an item is a stub by the URL of its <c>.strm</c>, even one the registry no longer knows,
    /// or by a <c>.strm</c> path in the federated folder before Jellyfin has read it.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for a stub.</returns>
    public bool IsStub(Guid itemId) => IsStub(_libraryManager.GetItemById(itemId));

    /// <summary>
    /// Tells whether an item is an audio stub: an audio <c>.strm</c> in the federated folder, which has no plugin URL, or one
    /// with the URL of the plugin in its file wherever it lies (row 582).
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for an audio stub.</returns>
    public bool IsAudioStub(Guid itemId) => _libraryManager.GetItemById(itemId) is Audio audio && IsAudioStub(audio);

    /// <summary>
    /// Tells whether an item is a placeholder: a book of the federated folder, which has no URL and is a stub by its path.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for a placeholder, even one the registry does not know.</returns>
    public bool IsPlaceholder(Guid itemId) => IsPlaceholder(_libraryManager.GetItemById(itemId));

    /// <summary>
    /// Tells whether an item is a photo stub: a photo of the federated folder, or one whose Primary is a thumbnail of the
    /// plugin while the folder is not known.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>True for a photo stub, even one the registry does not know.</returns>
    public bool IsPhotoStub(Guid itemId)
        => _libraryManager.GetItemById(itemId) is Photo photo
            && (PlaceholderPath(photo) is not null || PhotoThumbnails.IsThumbnail(photo.GetImageInfo(ImageType.Primary, 0)?.Path));

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
    /// Finds the stub an item plays, by the URL of its <c>.strm</c> or else by its path, and tells a federated <c>.strm</c> that the
    /// registry does not hold, which stock must not serve since it would send its signed URL.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="isUnknown">Whether the item is a federated <c>.strm</c> with no stub in the registry.</param>
    /// <returns>The stub, null for anything else.</returns>
    public StubEntry? Resolve(Guid itemId, out bool isUnknown)
    {
        isUnknown = false;
        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            // A book placeholder by its path (D63); no other item that is not a video is a stub here.
            return PlaceholderPath(item) is { } path ? _stubs.FindByPath(path) : null;
        }

        // Before the search: a registry read in between would otherwise count as one that does not hold the stub.
        var isUnread = _stubs.IsUnread;
        var isOurs = PlaybackRoutes.TryGetStubId(video.ShortcutPath, out var stubId);
        var stub = (isOurs ? _stubs.Find(stubId) : null) ?? (IsStrm(video.Path) ? _stubs.FindByPath(video.Path) : null);
        isUnknown = stub is null && IsStrm(video.Path) && IsFederated(video, isOurs, isUnread);
        return stub;
    }

    /// <summary>
    /// Finds the stub a playback of an item reaches, as <see cref="Resolve"/> does, and also an audio stub by its path.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="isUnknown">Whether the item is a federated <c>.strm</c> with no stub in the registry.</param>
    /// <returns>The stub, null for anything else.</returns>
    public StubEntry? ResolvePlayable(Guid itemId, out bool isUnknown) => Resolve(itemId, out isUnknown) ?? (IsAudioStub(itemId) ? FindFile(itemId) : null);

    /// <summary>
    /// Finds the stub of an item for its file and its visibility: a video stub, an audio stub by its path or else by the URL in
    /// its file (row 582), or a book placeholder by its path.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The stub, null for anything else.</returns>
    public StubEntry? FindFile(Guid itemId)
    {
        return _libraryManager.GetItemById(itemId) switch
        {
            Video video when PlaybackRoutes.TryGetStubId(video.ShortcutPath, out var stubId) => _stubs.Find(stubId),
            Audio audio when IsStrm(audio.Path) => (PathInFolder(audio.Path) is { } path ? _stubs.FindByPath(path) : null) ?? FindByStrmUrl(audio.Path),
            var item when PlaceholderPath(item) is { } path => _stubs.FindByPath(path),
            _ => null
        };
    }

    /// <summary>
    /// Finds the stub of an item as <see cref="FindFile"/> does, and that of a photo left under another federated folder
    /// by the name of its thumbnail, while a folder is set (row 553).
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The stub, null for anything else.</returns>
    public StubEntry? FindWithThumbnail(Guid itemId) => FindFile(itemId) ?? MovedPhotoStub(_libraryManager.GetItemById(itemId));

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

    // A link only between local films stays stock; a stub the registry lacks counts too (row 527), and so does any of our
    // URLs while the registry is unread (row 552).
    private bool IsLinkedToStub(Guid itemId)
    {
        var members = LinkedVersionsOf(itemId);
        if (members.Count < 2)
        {
            return false;
        }

        var root = _store.ReadOr(config => config.FederatedFolderPath, null);
        return members.Values.Any(member => FederatedPaths.IsInside(root, member.Path) || IsStub(member));
    }

    private bool IsStub(BaseItem? item)
    {
        return item switch
        {
            Video video => PlaybackRoutes.TryGetStubId(video.ShortcutPath, out _) || IsStrmInFolder(video.Path),
            Audio audio => IsAudioStub(audio),
            _ => IsPlaceholder(item)
        };
    }

    private bool IsPlaceholder(BaseItem? item) => PlaceholderPath(item) is not null;

    // An audio .strm in the federated folder, or one with our URL wherever it lies, also with no folder set (row 582).
    private bool IsAudioStub(Audio audio) => IsStrmInFolder(audio.Path) || StubIdInStrm(audio.Path) is not null;

    private StubEntry? FindByStrmUrl(string? path) => StubIdInStrm(path) is { } stubId ? _stubs.Find(stubId) : null;

    // Reads the stub id from the URL in a .strm; null for a file that cannot be read, is too long or holds another URL.
    private static string? StubIdInStrm(string? path)
    {
        if (!IsStrm(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            var buffer = new byte[MaxStrmBytes + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return read <= MaxStrmBytes && PlaybackRoutes.TryGetStubId(Encoding.UTF8.GetString(buffer, 0, read), out var stubId) ? stubId : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // A photo outside the federated folder whose Primary is a thumbnail of a photo stub the registry knows.
    private StubEntry? MovedPhotoStub(BaseItem? item)
        => item is Photo photo
            && PlaceholderPath(photo) is null
            && _store.ReadOr(config => config.FederatedFolderPath, null) is { Length: > 0 }
            && PhotoThumbnails.StubIdOf(photo.GetImageInfo(ImageType.Primary, 0)?.Path) is { } stubId
            && _stubs.Find(stubId) is { Kind: StubKind.Photo } stub
                ? stub
                : null;

    // The path of a book or a photo in the federated folder, also through an alias of it, spelled under the folder; else null.
    private string? PlaceholderPath(BaseItem? item)
    {
        if (item is not (Book or Photo) || item.Path is not { Length: > 0 } path || _store.ReadOr(config => config.FederatedFolderPath, null) is not { Length: > 0 } root)
        {
            return null;
        }

        return FederatedPaths.IsInside(root, path) ? path : RealPath.Rebase(root, path);
    }

    private static bool IsStrm(string? path) => path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true;

    // A .strm in the federated folder; while the settings cannot be read, any .strm, since stock would send its signed URL.
    // With no folder set none is in it (row 522).
    private bool IsStrmInFolder(string? path) => PathInFolder(path) is not null;

    // The path of a .strm in the federated folder, also through an alias of it, spelled under the folder; else null.
    private string? PathInFolder(string? path)
    {
        if (!IsStrm(path))
        {
            return null;
        }

        return _store.ReadOr(config => config.FederatedFolderPath, null) switch
        {
            null => path,
            { Length: 0 } => null,
            var root when FederatedPaths.IsInside(root, path) => path,
            var root => RealPath.Rebase(root, path!)
        };
    }

    // A .strm in the federated folder, or with our URL while the folder is not known or the registry is not read; while the
    // folder is not known, also one Jellyfin has not read. Any other .strm is the administrator's.
    private bool IsFederated(Video video, bool isOurs, bool isUnread)
    {
        var root = _store.ReadOr(config => config.FederatedFolderPath, null);
        return FederatedPaths.IsInside(root, video.Path)
            || (isOurs && (root is null || isUnread))
            || (root is null && string.IsNullOrEmpty(video.ShortcutPath));
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
