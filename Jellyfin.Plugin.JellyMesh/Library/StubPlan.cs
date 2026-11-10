using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What a run has to do.
/// </summary>
/// <param name="State">The registry after the plan, with what is not yet on disk marked so.</param>
/// <param name="Stubs">The film and episode stubs that should exist.</param>
/// <param name="Series">The series that should exist.</param>
/// <param name="Removals">The stubs whose title is really gone. They are still in <paramref name="State"/>.</param>
/// <param name="Held">The libraries that came back empty.</param>
/// <param name="Unplaceable">How many episodes have no season or number, so they have no place in a series.</param>
public sealed record StubPlan(
    StubState State,
    IReadOnlyList<StubWork> Stubs,
    IReadOnlyList<SeriesWork> Series,
    IReadOnlyList<StubEntry> Removals,
    IReadOnlyList<HeldLibrary> Held,
    int Unplaceable)
{
    /// <summary>
    /// Gets how many stubs were merged into another. They stay until the post-scan task has handed their watch state over.
    /// </summary>
    public int MergedStubs { get; init; }

    /// <summary>
    /// Gets how many stubs are kept because none of their servers is configured and none was removed from the page.
    /// </summary>
    public int Unconfigured { get; init; }
}
