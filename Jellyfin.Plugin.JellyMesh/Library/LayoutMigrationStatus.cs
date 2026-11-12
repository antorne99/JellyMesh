using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Where the manual migration stands, for the banner (Q40, Q48, Q49).
/// </summary>
/// <param name="Step">The step the admin is at.</param>
/// <param name="Libraries">The libraries on the old paths, with the new paths they lack.</param>
/// <param name="OldTitles">The stubs and series still in the old folders.</param>
public sealed record LayoutMigrationStatus(LayoutMigrationStep Step, IReadOnlyList<LayoutMigrationLibrary> Libraries, int OldTitles)
{
    /// <summary>Gets the old titles the last run could not carry until a scan, null before the first run.</summary>
    public int? Waiting { get; init; }

    /// <summary>Gets the old titles the last run could not carry for a reason a scan does not fix, null before the first run (Q216).</summary>
    public int? Blocked { get; init; }
}
