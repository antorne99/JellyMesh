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
    /// already linked as wanted is not saved.
    /// </summary>
    /// <param name="primary">The primary.</param>
    /// <param name="alternates">The other members, in the order to link them.</param>
    /// <param name="states">The current links of the members and of the items they point to.</param>
    /// <returns>The plan.</returns>
    public static VersionLinkPlan Plan(Guid primary, IReadOnlyList<Guid> alternates, IReadOnlyCollection<VersionLinkState> states)
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

        var updates = new List<VersionLinkUpdate>();
        foreach (var outsider in Outsiders(alternates.Append(primary).Select(StateOf), members))
        {
            if (known.TryGetValue(outsider, out var state))
            {
                var wanted = new VersionLinkUpdate(
                    outsider,
                    state.PrimaryVersionId is { } id && members.Contains(id) ? null : state.PrimaryVersionId,
                    state.LinkedIds.Where(linked => !members.Contains(linked)).ToList());
                AddIfChanged(updates, state, wanted);
            }
        }

        foreach (var alternate in alternates)
        {
            AddIfChanged(updates, StateOf(alternate), new VersionLinkUpdate(alternate, primary, []));
        }

        AddIfChanged(updates, StateOf(primary), new VersionLinkUpdate(primary, null, alternates));

        var joined = alternates.Where(alternate => StateOf(alternate).PrimaryVersionId != primary).ToList();
        if (StateOf(primary).PrimaryVersionId is not null)
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
