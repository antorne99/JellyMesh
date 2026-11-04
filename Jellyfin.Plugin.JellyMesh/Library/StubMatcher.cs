using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Finds the stub of a content identity. An identity key can change when a source brings a better ID, and a stub found
/// under a new key must stay the same stub: a new one would be a new Jellyfin item, and the watch state would be lost.
/// So a stub is also found from the remote items it had.
/// </summary>
public sealed class StubMatcher
{
    private readonly Dictionary<string, StubEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid ServerId, string ItemId), StubEntry> _byItem = [];
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="StubMatcher"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    public StubMatcher(IEnumerable<StubEntry> stubs)
    {
        ArgumentNullException.ThrowIfNull(stubs);

        foreach (var stub in stubs)
        {
            _byKey.TryAdd(stub.IdentityKey, stub);
            foreach (var source in stub.Sources)
            {
                _byItem.TryAdd((source.ServerId, source.ItemId), stub);
            }
        }
    }

    /// <summary>
    /// Finds the stub of a content and claims it, so that no other content gets the same one.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The stub, null if it has none.</returns>
    public StubEntry? Claim(ResolvedContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (_byKey.TryGetValue(content.Identity.Key, out var stub) && _claimed.Add(stub.Id))
        {
            return stub;
        }

        foreach (var item in content.RemoteItems)
        {
            if (_byItem.TryGetValue((item.ServerId, item.ItemId), out stub) && _claimed.Add(stub.Id))
            {
                return stub;
            }
        }

        return null;
    }

    /// <summary>
    /// Tells whether a content claimed a stub.
    /// </summary>
    /// <param name="stub">The stub.</param>
    /// <returns>Whether it was claimed.</returns>
    public bool IsClaimed(StubEntry stub)
    {
        ArgumentNullException.ThrowIfNull(stub);
        return _claimed.Contains(stub.Id);
    }
}
