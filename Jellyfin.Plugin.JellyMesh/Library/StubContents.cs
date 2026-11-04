using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Turns the contents of a catalog, one per episode, into one per stub. The dedup decides each episode on its own (§4.2
/// rule 4), so a file with several episodes is a source for each of them. Here, consecutive episodes won by the same
/// source become one stub covering the range, which is what the remote file really is (decision 13a).
/// </summary>
/// <remarks>
/// A file covering episodes that are decided otherwise cannot be shown as a range. If it wins only E02 of E01-E02, because
/// E01 is local or a better copy exists, it gets a plain E02 stub that plays the whole file, from E01.
/// </remarks>
public static class StubContents
{
    /// <summary>
    /// Groups contents. Consecutive local episodes that share a remote file are grouped too, so that the stub of that file
    /// hands its state over to all of them.
    /// </summary>
    /// <param name="contents">The contents, one per episode.</param>
    /// <returns>The contents, one per stub, sorted by key.</returns>
    public static IReadOnlyList<ResolvedContent> Group(IEnumerable<ResolvedContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var ordered = contents
            .OrderBy(content => content.Identity.WorkKey, StringComparer.Ordinal)
            .ThenBy(content => content.Identity.SeasonNumber)
            .ThenBy(content => content.Identity.EpisodeNumber)
            .ThenBy(content => content.Identity.Key, StringComparer.Ordinal);

        var groups = new List<ResolvedContent>();
        foreach (var content in ordered)
        {
            if (groups.Count > 0 && CanJoin(groups[^1], content))
            {
                groups[^1] = Join(groups[^1], content);
            }
            else
            {
                groups.Add(content);
            }
        }

        return groups.OrderBy(content => content.Identity.Key, StringComparer.Ordinal).ToList();
    }

    private static bool CanJoin(ResolvedContent group, ResolvedContent next)
    {
        var (first, second) = (group.Identity, next.Identity);
        if (first.Kind != ContentKind.Episode
            || !string.Equals(first.WorkKey, second.WorkKey, StringComparison.Ordinal)
            || first.SeasonNumber is null
            || first.SeasonNumber != second.SeasonNumber
            || second.EpisodeNumber != (group.EpisodeNumberEnd ?? first.EpisodeNumber) + 1)
        {
            return false;
        }

        return group.Winner is { } winner
            ? next.Winner is { } other && IsSameSource(winner, other)
            : next.IsLocal && group.RemoteItems.Intersect(next.RemoteItems).Any();
    }

    private static ResolvedContent Join(ResolvedContent group, ResolvedContent next)
    {
        return group with
        {
            // A source that lacks one of the episodes could not stand in for the whole stub: it would stop half way.
            Backups = group.Backups.Where(backup => next.Backups.Any(other => IsSameSource(backup, other))).ToList(),
            EpisodeNumberEnd = next.Identity.EpisodeNumber,
            LocalItemIds = group.LocalItemIds.Union(next.LocalItemIds).ToList(),
            RemoteItems = group.RemoteItems.Union(next.RemoteItems).ToList()
        };
    }

    private static bool IsSameSource(ResolvedSource left, ResolvedSource right)
    {
        return left.ServerId == right.ServerId
            && string.Equals(left.ItemId, right.ItemId, StringComparison.Ordinal)
            && string.Equals(left.MediaSourceId, right.MediaSourceId, StringComparison.Ordinal);
    }
}
