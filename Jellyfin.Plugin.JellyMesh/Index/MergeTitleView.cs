using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// A title of a <see cref="MergeCaseView"/>.
/// </summary>
/// <param name="WorkKey">What to send back to merge it. It holds until the catalog changes.</param>
/// <param name="HasLocalCopy">Whether a copy is in the local library.</param>
/// <param name="MergeIds">The merges that made this title, empty for one nobody merged.</param>
/// <param name="Copies">Its copies.</param>
public sealed record MergeTitleView(string WorkKey, bool HasLocalCopy, IReadOnlyList<Guid> MergeIds, IReadOnlyList<MergeCopyView> Copies);
