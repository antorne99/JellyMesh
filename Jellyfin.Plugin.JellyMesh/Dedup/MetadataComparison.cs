using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// Decides whether the metadata of two film copies confirm that they are the same film (decision 13).
/// </summary>
/// <remarks>
/// <para>
/// A wrong merge hides a title (§4.2 rule 1) and a missed one only shows it twice, so in doubt the copies stay separate.
/// Only fields present on both sides are compared; a field missing on either side says nothing. Then:
/// </para>
/// <list type="bullet">
/// <item>any compared field that differs contradicts the match, whatever the others say;</item>
/// <item>
/// otherwise at least one of original title, runtime, people or plot must agree for the match to be confirmed. Genres and
/// studios can contradict a match but never confirm one, because too many films share them;
/// </item>
/// <item>anything else, including no field to compare at all, is inconclusive and does not merge.</item>
/// </list>
/// <para>
/// A field agrees as follows. Original titles must be the same once case, accents and punctuation are ignored, and
/// articles count here, so that "La La Land" and "Land" differ. Runtimes must be within 1%, and at least 2 seconds apart
/// is always allowed. Plots must share half of their distinct words. People, genres and studios must share half of the
/// shorter list, so a full cast and a short one can agree.
/// </para>
/// <para>
/// So a copy with nothing but a runtime, the usual case for a file no fetcher recognised, is confirmed by a runtime that
/// agrees. Language counts against a match: a plot or genres in another language differ, which leaves a duplicate. That is
/// the accepted direction.
/// </para>
/// </remarks>
internal static class MetadataComparison
{
    private const int RuntimeTolerancePercent = 1;
    private const long MinimumRuntimeToleranceTicks = 2 * TimeSpan.TicksPerSecond;
    private const double MinimumShare = 0.5;

    private enum Field
    {
        Missing,
        Agrees,
        Differs
    }

    /// <summary>
    /// Compares two copies. The result does not depend on which is which.
    /// </summary>
    /// <param name="left">One copy.</param>
    /// <param name="right">The other.</param>
    /// <returns>The verdict.</returns>
    public static MetadataVerdict Compare(ComparableMetadata left, ComparableMetadata right)
    {
        // Weak fields first and people last: people may need a database read, and a contradiction makes it pointless.
        var weak = new[] { CompareLists(left.Genres, right.Genres), CompareLists(left.Studios, right.Studios) };
        if (weak.Contains(Field.Differs))
        {
            return MetadataVerdict.Contradicted;
        }

        var strong = new[] { CompareOriginalTitles(left, right), CompareRuntimes(left, right), CompareOverviews(left, right) };
        if (strong.Contains(Field.Differs))
        {
            return MetadataVerdict.Contradicted;
        }

        var people = CompareLists(left.People.Value, right.People.Value);
        if (people == Field.Differs)
        {
            return MetadataVerdict.Contradicted;
        }

        return people == Field.Agrees || strong.Contains(Field.Agrees) ? MetadataVerdict.Confirmed : MetadataVerdict.Inconclusive;
    }

    private static Field CompareOriginalTitles(ComparableMetadata left, ComparableMetadata right)
    {
        var first = TitleNormalizer.Words(left.OriginalTitle);
        var second = TitleNormalizer.Words(right.OriginalTitle);
        if (first.Count == 0 || second.Count == 0)
        {
            return Field.Missing;
        }

        return first.SequenceEqual(second, StringComparer.Ordinal) ? Field.Agrees : Field.Differs;
    }

    private static Field CompareRuntimes(ComparableMetadata left, ComparableMetadata right)
    {
        if (left.RunTimeTicks is not > 0 || right.RunTimeTicks is not > 0)
        {
            return Field.Missing;
        }

        var (first, second) = (left.RunTimeTicks.Value, right.RunTimeTicks.Value);
        var tolerance = Math.Max(MinimumRuntimeToleranceTicks, Math.Max(first, second) / 100 * RuntimeTolerancePercent);
        return Math.Abs(first - second) <= tolerance ? Field.Agrees : Field.Differs;
    }

    private static Field CompareOverviews(ComparableMetadata left, ComparableMetadata right)
    {
        var first = TitleNormalizer.Words(left.Overview).ToHashSet(StringComparer.Ordinal);
        var second = TitleNormalizer.Words(right.Overview).ToHashSet(StringComparer.Ordinal);
        if (first.Count == 0 || second.Count == 0)
        {
            return Field.Missing;
        }

        var shared = first.Count(second.Contains);
        var all = first.Count + second.Count - shared;
        return shared >= all * MinimumShare ? Field.Agrees : Field.Differs;
    }

    private static Field CompareLists(IEnumerable<string> left, IEnumerable<string> right)
    {
        var first = Names(left);
        var second = Names(right);
        if (first.Count == 0 || second.Count == 0)
        {
            return Field.Missing;
        }

        var shared = first.Count(second.Contains);
        return shared >= Math.Min(first.Count, second.Count) * MinimumShare ? Field.Agrees : Field.Differs;
    }

    private static HashSet<string> Names(IEnumerable<string> names)
    {
        return names
            .Select(name => string.Join(' ', TitleNormalizer.Words(name)))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }
}
