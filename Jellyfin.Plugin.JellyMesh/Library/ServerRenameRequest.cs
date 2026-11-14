using System.Collections.Generic;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The answer to a rename request, with the background work when it started.
/// </summary>
/// <param name="Outcome">The answer.</param>
/// <param name="Job">The background work, already complete when none started.</param>
/// <param name="Libraries">The automatic libraries the rename renames, for the confirmation.</param>
public sealed record ServerRenameRequest(ServerRenameOutcome Outcome, Task Job, IReadOnlyList<string> Libraries)
{
    /// <summary>Builds an answer for which no work started.</summary>
    /// <param name="outcome">The answer.</param>
    /// <returns>The request.</returns>
    public static ServerRenameRequest Refused(ServerRenameOutcome outcome) => new(outcome, Task.CompletedTask, []);
}
