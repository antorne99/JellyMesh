using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// A title of a <see cref="MergeCase"/>.
/// </summary>
/// <param name="WorkKey">The work key. It holds for this result only: see <see cref="ContentIdentity.Key"/>.</param>
/// <param name="MergeIds">The merges that made this title, empty for one nobody merged.</param>
/// <param name="Copies">Its copies, sorted.</param>
public sealed record MergeCaseWork(string WorkKey, IReadOnlyList<Guid> MergeIds, IReadOnlyList<WorkCopy> Copies)
{
    /// <summary>Gets a value indicating whether a copy is in the local library.</summary>
    public bool HasLocalCopy => Copies.Any(copy => copy.IsLocal);
}
