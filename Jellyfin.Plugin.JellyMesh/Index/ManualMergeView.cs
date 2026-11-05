using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Index;

/// <summary>
/// A merge the administrator made, as it stands in the catalog now.
/// </summary>
/// <param name="Id">The merge.</param>
/// <param name="CreatedUtc">When it was made.</param>
/// <param name="IsInEffect">Whether it joins titles now. If not, it waits for its copies, or the IDs do its work.</param>
/// <param name="Copies">Its copies.</param>
public sealed record ManualMergeView(Guid Id, DateTimeOffset CreatedUtc, bool IsInEffect, IReadOnlyList<MergeCopyView> Copies);
