using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The answer to a switch request, with the background work when it started.
/// </summary>
/// <param name="Outcome">The answer.</param>
/// <param name="Job">The background work, already complete when none started.</param>
/// <param name="ToDelete">The libraries a confirmation would delete.</param>
public sealed record AutomaticLibrariesRequest(AutomaticLibrariesOutcome Outcome, Task Job, IReadOnlyList<string> ToDelete)
{
    /// <summary>Builds an answer for which no work started.</summary>
    /// <param name="outcome">The answer.</param>
    /// <returns>The request.</returns>
    public static AutomaticLibrariesRequest Refused(AutomaticLibrariesOutcome outcome) => new(outcome, Task.CompletedTask, []);
}
