using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
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
    private readonly ILookup<string, StubEntry> _byKey;
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

        // Two stubs of one title, after their local libraries became one: the one a plan gave the copies of both stays.
        _byKey = _stubs
            .OrderByDescending(stub => stub.Sources.Count)
            .ThenBy(stub => stub.Id, StringComparer.Ordinal)
            .ToLookup(stub => stub.IdentityKey, StringComparer.Ordinal);

        foreach (var stub in _stubs)
        {
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

        return ClaimOwn(content) ?? ClaimFirst(content, HoldersOf(content));
    }

    /// <summary>
    /// Claims the stubs of a whole catalog. Contents with a winner choose first, because a stub that can still serve one
    /// must not go just because another content sharing its remote item turned local. A stub goes to the content of its own
    /// key before one that shares an item. Then every stub left whose remote item now belongs to a content with a stub is
    /// merged into that stub: two copies that became one title. A stub of several episodes is merged into the stub of each
    /// (§10 row 537).
    /// </summary>
    /// <param name="contents">The contents.</param>
    /// <returns>One claim per content, those with a winner first.</returns>
    public IReadOnlyList<StubClaim> ClaimAll(IEnumerable<ResolvedContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var claims = new List<StubClaim>();
        foreach (var part in contents.ToLookup(content => content.IsLocal).OrderBy(part => part.Key))
        {
            var start = claims.Count;
            claims.AddRange(part.Select(content => new StubClaim(content, ClaimFirst(content, _byKey[content.Identity.Key]))));
            for (var index = start; index < claims.Count; index++)
            {
                if (claims[index].Stub is null)
                {
                    claims[index] = claims[index] with { Stub = ClaimOwn(claims[index].Content) };
                }
            }

            var owners = OwnersOf(claims);
            for (var index = start; index < claims.Count; index++)
            {
                if (claims[index].Stub is null)
                {
                    var content = claims[index].Content;
                    claims[index] = claims[index] with { Stub = ClaimFirst(content, HoldersOf(content).Where(stub => !TargetsOf(stub, claims, owners).Exists(owner => Covers(claims[owner].Content, stub)))) };
                }
            }
        }

        var all = OwnersOf(claims);
        foreach (var stub in _stubs.Where(stub => !_claimed.Contains(stub.Id)).ToList())
        {
            foreach (var owner in TargetsOf(stub, claims, all))
            {
                claims[owner] = claims[owner] with { Merged = [.. claims[owner].Merged, stub] };
                _claimed.Add(stub.Id);
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

    // The claims with a stub, by the remote items of their content, in order.
    private static ILookup<(Guid ServerId, string ItemId), int> OwnersOf(List<StubClaim> claims) => claims
        .SelectMany((claim, index) => claim.Stub is null ? [] : claim.Content.RemoteItems.Select(item => (Item: (item.ServerId, item.ItemId), Index: index)))
        .ToLookup(entry => entry.Item, entry => entry.Index);

    // The claims a stub is merged into. A file of several episodes is an item of each: a stub of several goes to every
    // content inside it, and one of a single episode to the content of its key, else to the one that covers it.
    private static List<int> TargetsOf(StubEntry stub, List<StubClaim> claims, ILookup<(Guid ServerId, string ItemId), int> owners)
    {
        var sharing = stub.Sources.SelectMany(source => owners[(source.ServerId, source.ItemId)]).Distinct().ToList();
        var inside = stub.EpisodeNumberEnd is null ? [] : sharing.FindAll(owner => IsInside(claims[owner].Content, stub));
        if (inside.Count > 0)
        {
            return inside;
        }

        return sharing.OrderBy(Rank).Take(1).ToList();

        int Rank(int owner) => string.Equals(claims[owner].Content.Identity.Key, stub.IdentityKey, StringComparison.Ordinal) ? 0
            : Covers(claims[owner].Content, stub) ? 1
            : 2;
    }

    // Whether a content is of the episodes of a stub, or of some of them. Any content is, for a film.
    private static bool Covers(ResolvedContent content, StubEntry stub) => content.Identity.Kind != ContentKind.Episode
        || (IsSeasonOf(content, stub) && FirstOf(content) <= LastOf(stub) && LastOf(content) >= stub.EpisodeNumber);

    private static bool IsInside(ResolvedContent content, StubEntry stub)
        => IsSeasonOf(content, stub) && FirstOf(content) >= stub.EpisodeNumber && LastOf(content) <= LastOf(stub);

    private static bool IsSame(ResolvedContent content, StubEntry stub)
        => content.Identity.Kind != ContentKind.Episode || (IsSeasonOf(content, stub) && FirstOf(content) == stub.EpisodeNumber && LastOf(content) == LastOf(stub));

    private static bool IsSeasonOf(ResolvedContent content, StubEntry stub) => content.Identity.SeasonNumber is { } season && season == stub.SeasonNumber;

    private static int? FirstOf(ResolvedContent content) => content.Identity.EpisodeNumber;

    private static int? LastOf(ResolvedContent content) => content.EpisodeNumberEnd ?? content.Identity.EpisodeNumber;

    private static int? LastOf(StubEntry stub) => stub.EpisodeNumberEnd ?? stub.EpisodeNumber;

    // The stub of the key of a content, else one with an item of it that covers it: first a stub of the same episodes.
    private StubEntry? ClaimOwn(ResolvedContent content)
        => ClaimFirst(content, _byKey[content.Identity.Key]) ?? ClaimFirst(content, HoldersOf(content).Where(stub => Covers(content, stub)));

    private StubEntry? ClaimFirst(ResolvedContent content, IEnumerable<StubEntry> stubs)
    {
        var stub = stubs.Where(stub => !_claimed.Contains(stub.Id)).MinBy(stub => IsSame(content, stub) ? 0 : 1);
        return stub is not null && _claimed.Add(stub.Id) ? stub : null;
    }

    private IEnumerable<StubEntry> HoldersOf(ResolvedContent content)
        => content.RemoteItems.SelectMany(item => _byItem.GetValueOrDefault((item.ServerId, item.ItemId)) ?? []).Distinct().Where(stub => !_claimed.Contains(stub.Id));
}
