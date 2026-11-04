using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Finds the stub of a content identity. An identity key can change when a source brings a better ID, and a stub found
/// under a new key must stay the same stub: a new one would be a new Jellyfin item, and the watch state would be lost.
/// So a stub is also found from the remote items it had.
/// </summary>
public sealed class StubMatcher
{
    private readonly List<StubEntry> _stubs;
    private readonly Dictionary<string, StubEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid ServerId, string ItemId), List<StubEntry>> _byItem = [];
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="StubMatcher"/> class.
    /// </summary>
    /// <param name="stubs">The stubs.</param>
    public StubMatcher(IEnumerable<StubEntry> stubs)
    {
        ArgumentNullException.ThrowIfNull(stubs);

        _stubs = stubs.ToList();
        foreach (var stub in _stubs)
        {
            _byKey.TryAdd(stub.IdentityKey, stub);
            foreach (var source in stub.Sources)
            {
                if (!_byItem.TryGetValue((source.ServerId, source.ItemId), out var holders))
                {
                    holders = [];
                    _byItem[(source.ServerId, source.ItemId)] = holders;
                }

                holders.Add(stub);
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
            foreach (var holder in _byItem.GetValueOrDefault((item.ServerId, item.ItemId)) ?? [])
            {
                if (_claimed.Add(holder.Id))
                {
                    return holder;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Claims the stubs of a whole catalog. Contents with a winner choose first, because a stub that can still serve one
    /// must not go just because another content sharing its remote item turned local. Then every stub left whose remote
    /// item now belongs to a content with a stub is merged into that stub: two copies that became one title.
    /// </summary>
    /// <param name="contents">The contents.</param>
    /// <returns>One claim per content, those with a winner first.</returns>
    public IReadOnlyList<StubClaim> ClaimAll(IEnumerable<ResolvedContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var claims = contents
            .OrderBy(content => content.IsLocal)
            .Select(content => new StubClaim(content, Claim(content)))
            .ToList();

        var owners = new Dictionary<(Guid ServerId, string ItemId), int>();
        for (var index = 0; index < claims.Count; index++)
        {
            if (claims[index].Stub is not null)
            {
                foreach (var item in claims[index].Content.RemoteItems)
                {
                    owners.TryAdd((item.ServerId, item.ItemId), index);
                }
            }
        }

        foreach (var stub in _stubs.Where(stub => !_claimed.Contains(stub.Id)))
        {
            foreach (var source in stub.Sources)
            {
                if (owners.TryGetValue((source.ServerId, source.ItemId), out var owner))
                {
                    claims[owner] = claims[owner] with { Merged = [.. claims[owner].Merged, stub] };
                    _claimed.Add(stub.Id);
                    break;
                }
            }
        }

        return claims;
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
