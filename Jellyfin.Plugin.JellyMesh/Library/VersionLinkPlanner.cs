using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Decides the primary version of a title and the links that make Jellyfin show its copies as one item (§3.4, Q43).
/// </summary>
public static class VersionLinkPlanner
{
    /// <summary>
    /// Chooses the primary: the local copy, else the recorded stub, else the best stub, which is then recorded. A
    /// recorded stub with no item lends its place to the best one and stays recorded.
    /// </summary>
    /// <param name="locals">The items of the local copies found in the library.</param>
    /// <param name="stubs">The item of each stub found in the library, by stub ID.</param>
    /// <param name="primaryStubId">The stub recorded as primary.</param>
    /// <param name="best">Picks the best of some stubs.</param>
    /// <returns>The primary.</returns>
    public static VersionPrimary ChoosePrimary(
        IReadOnlyList<Guid> locals,
        IReadOnlyDictionary<string, Guid> stubs,
        string? primaryStubId,
        Func<IReadOnlyList<string>, string> best)
    {
        ArgumentNullException.ThrowIfNull(locals);
        ArgumentNullException.ThrowIfNull(stubs);
        ArgumentNullException.ThrowIfNull(best);

        if (locals.Count > 0)
        {
            return new VersionPrimary(locals[0], null);
        }

        if (primaryStubId is not null && stubs.TryGetValue(primaryStubId, out var recorded))
        {
            return new VersionPrimary(recorded, primaryStubId);
        }

        var chosen = best(stubs.Keys.Order(StringComparer.Ordinal).ToList());
        return new VersionPrimary(stubs[chosen], primaryStubId ?? chosen);
    }

    /// <summary>
    /// Plans the saves that link the alternates to the primary and detach any other item linked to them. An item
    /// already linked as wanted is not saved. A link between two films that are not stubs is never changed (§10 row 148).
    /// </summary>
    /// <param name="primary">The primary.</param>
    /// <param name="alternates">The other members, in the order to link them.</param>
    /// <param name="states">The current links of the members and of the items they point to.</param>
    /// <param name="isFilm">Whether an item is a film or episode that is not a stub; none is when null.</param>
    /// <returns>The plan.</returns>
    public static VersionLinkPlan Plan(Guid primary, IReadOnlyList<Guid> alternates, IReadOnlyCollection<VersionLinkState> states, Func<Guid, bool>? isFilm = null)
    {
        ArgumentNullException.ThrowIfNull(alternates);
        ArgumentNullException.ThrowIfNull(states);

        var known = new Dictionary<Guid, VersionLinkState>();
        foreach (var state in states)
        {
            known.TryAdd(state.ItemId, state);
        }

        var members = new HashSet<Guid>(alternates) { primary };
        VersionLinkState StateOf(Guid id) => known.GetValueOrDefault(id) ?? new VersionLinkState(id, null, []);
        bool Kept(Guid from, Guid to) => isFilm is not null && isFilm(from) && isFilm(to);
        List<Guid> KeptLinks(VersionLinkState state) => state.LinkedIds.Where(id => !members.Contains(id) && Kept(state.ItemId, id)).ToList();

        var updates = new List<VersionLinkUpdate>();
        foreach (var outsider in Outsiders(alternates.Append(primary).Select(StateOf), members))
        {
            if (known.TryGetValue(outsider, out var state))
            {
                var wanted = new VersionLinkUpdate(
                    outsider,
                    state.PrimaryVersionId is { } id && members.Contains(id) && !Kept(outsider, id) ? null : state.PrimaryVersionId,
                    state.LinkedIds.Where(linked => !members.Contains(linked) || Kept(outsider, linked)).ToList());
                AddIfChanged(updates, state, wanted);
            }
        }

        foreach (var alternate in alternates)
        {
            AddIfChanged(updates, StateOf(alternate), new VersionLinkUpdate(alternate, primary, KeptLinks(StateOf(alternate))));
        }

        var current = StateOf(primary);
        var above = current.PrimaryVersionId is { } over && !members.Contains(over) && Kept(primary, over) ? over : (Guid?)null;
        AddIfChanged(updates, current, new VersionLinkUpdate(primary, above, [.. alternates, .. KeptLinks(current)]));

        var joined = alternates.Where(alternate => StateOf(alternate).PrimaryVersionId != primary).ToList();
        if (current.PrimaryVersionId != above)
        {
            joined.Add(primary);
        }

        return new VersionLinkPlan(updates, joined);
    }

    private static IEnumerable<Guid> Outsiders(IEnumerable<VersionLinkState> members, HashSet<Guid> group)
    {
        var seen = new HashSet<Guid>();
        foreach (var state in members)
        {
            var linked = state.PrimaryVersionId is { } primary ? state.LinkedIds.Prepend(primary) : state.LinkedIds;
            foreach (var id in linked.Where(id => !group.Contains(id) && seen.Add(id)))
            {
                yield return id;
            }
        }
    }

    private static void AddIfChanged(List<VersionLinkUpdate> updates, VersionLinkState state, VersionLinkUpdate wanted)
    {
        var same = state.PrimaryVersionId == wanted.PrimaryVersionId
            && state.LinkedIds.Count == wanted.LinkedIds.Count
            && state.LinkedIds.ToHashSet().SetEquals(wanted.LinkedIds);
        if (!same)
        {
            updates.Add(wanted);
        }
    }
}
