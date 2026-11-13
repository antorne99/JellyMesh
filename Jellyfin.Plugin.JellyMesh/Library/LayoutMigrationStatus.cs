using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Where the migration stands, for the banner (Q40, Q48, Q49, Q55-Q57).
/// </summary>
/// <param name="Step">The step of the manual migration, which also tells the steps left to do by hand.</param>
/// <param name="Libraries">The libraries on the old paths, with the new paths they lack.</param>
/// <param name="OldTitles">The stubs and series still in the old folders.</param>
public sealed record LayoutMigrationStatus(LayoutMigrationStep Step, IReadOnlyList<LayoutMigrationLibrary> Libraries, int OldTitles)
{
    /// <summary>Gets the old titles the last run could not carry until a scan, null before the first run.</summary>
    public int? Waiting { get; init; }

    /// <summary>Gets the old titles the last run could not carry for a reason a scan does not fix, null before the first run (Q216).</summary>
    public int? Blocked { get; init; }

    /// <summary>Gets the old titles kept only by remote libraries read empty, null before the first run (Q270).</summary>
    public int? Held { get; init; }

    /// <summary>
    /// Gets the server folders in no library and not already listed as missing from a library on an old path.
    /// </summary>
    public IReadOnlyList<string> UnlinkedFolders { get; init; } = [];

    /// <summary>Gets the mode the admin chose, <see cref="LayoutMigrationMode.None"/> while the banner asks.</summary>
    public LayoutMigrationMode Mode { get; init; }

    /// <summary>Gets the step of the automatic migration, null unless it is chosen.</summary>
    public AutomaticMigrationStep? AutomaticStep { get; init; }

    /// <summary>Gets a value indicating whether the automatic migration is running.</summary>
    public bool Running { get; init; }

    /// <summary>Gets why the automatic migration stopped at its step.</summary>
    public AutomaticMigrationError Error { get; init; }

    /// <summary>Gets the library where it stopped, null when the error names none.</summary>
    public string? ErrorLibrary { get; init; }
}
