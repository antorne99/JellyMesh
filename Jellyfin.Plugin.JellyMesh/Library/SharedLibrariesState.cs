using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What the page shows about the shared libraries (§10 row 442).
/// </summary>
/// <param name="Enabled">Whether the option is on.</param>
/// <param name="Busy">Whether a creation or a deletion is running.</param>
/// <param name="Libraries">The shared libraries, made or adopted.</param>
/// <param name="Released">The libraries left to the admin.</param>
/// <param name="Error">What went wrong in the last run.</param>
public sealed record SharedLibrariesState(
    bool Enabled,
    bool Busy,
    IReadOnlyList<AutomaticLibraryStatus> Libraries,
    IReadOnlyList<string> Released,
    AutomaticLibrariesError Error)
{
    /// <summary>Gets the libraries of the admin that kept the last check from making or adopting a shared library, and why (D43).</summary>
    public IReadOnlyList<LibraryConflict> Conflicts { get; init; } = [];

    /// <summary>Gets the shared libraries whose blocks could not be written and that could not be deleted, so no scan runs (D42).</summary>
    public IReadOnlyList<string> Unblocked { get; init; } = [];
}
