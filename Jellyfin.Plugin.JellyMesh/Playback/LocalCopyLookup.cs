using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Finds the local copy of a stub that the post-scan task has not replaced yet. That window lasts until the next full scan:
/// a realtime change or a refresh of one folder adds the copy without running the task.
/// </summary>
public sealed class LocalCopyLookup
{
    private readonly IFederationResolver _resolver;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalCopyLookup"/> class.
    /// </summary>
    /// <param name="resolver">The resolver.</param>
    /// <param name="libraryManager">The library manager.</param>
    public LocalCopyLookup(IFederationResolver resolver, ILibraryManager libraryManager)
    {
        _resolver = resolver;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Finds a local copy the user may play. This reads the whole library, so it belongs on the failure path only. A copy in
    /// a library the user cannot see does not count: the stub must not become a way into it.
    /// </summary>
    /// <param name="stub">The stub.</param>
    /// <param name="user">The user.</param>
    /// <returns>The local item, null if there is none.</returns>
    public Video? Find(StubEntry stub, User user)
    {
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
