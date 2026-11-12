using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Finds the local copy of a stub: a local member of its group, or for a stub in no group, one that the post-scan task has
/// not matched yet. That window lasts until the next full scan.
/// </summary>
public sealed class LocalCopyLookup
{
    private readonly IFederationResolver _resolver;
    private readonly ILibraryManager _libraryManager;
    private readonly IStubRegistry _stubs;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalCopyLookup"/> class.
    /// </summary>
    /// <param name="resolver">The resolver.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="stubs">The registry of stubs and groups.</param>
    public LocalCopyLookup(IFederationResolver resolver, ILibraryManager libraryManager, IStubRegistry stubs)
    {
        _resolver = resolver;
        _libraryManager = libraryManager;
        _stubs = stubs;
    }

    /// <summary>
    /// Finds a local copy the user may play. For a stub in no group this reads the whole library, so it belongs on the failure
    /// path only. A copy in a library the user cannot see does not count: the stub must not become a way into it.
    /// </summary>
    /// <param name="stub">The stub.</param>
    /// <param name="user">The user.</param>
    /// <returns>The local item, null if there is none.</returns>
    public Video? Find(StubEntry stub, User user)
    {
        ArgumentNullException.ThrowIfNull(stub);

        if (_stubs.FindGroup(stub.Id) is { LocalItemIds.Count: > 0 } group)
        {
            return group.LocalItemIds
                .Select(id => _libraryManager.GetItemById<BaseItem>(id, user))
                .OfType<Video>()
                .FirstOrDefault();
        }

        var contents = _resolver.Resolve().Contents;
        var matcher = new StubMatcher([stub]);

        // A stub that still has content to serve is not replaced, even if part of it has arrived: one file can hold two episodes.
        if (contents.Any(content => !content.IsLocal && matcher.Claim(content) is not null))
        {
            return null;
        }

        var local = contents.FirstOrDefault(content => content.IsLocal && matcher.Claim(content) is not null);
        return local?.LocalItemIds
            .Select(id => _libraryManager.GetItemById<BaseItem>(id, user))
            .OfType<Video>()
            .FirstOrDefault();
    }
}
