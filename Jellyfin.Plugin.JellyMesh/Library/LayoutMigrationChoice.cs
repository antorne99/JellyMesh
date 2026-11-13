using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The outcome of a choice in the migration banner, with the work it started.
/// </summary>
/// <param name="Outcome">What the choice did.</param>
/// <param name="Job">The work it started in the background, completed when there is none.</param>
public sealed record LayoutMigrationChoice(LayoutMigrationChoiceOutcome Outcome, Task Job)
{
    /// <summary>
    /// A choice that started nothing.
    /// </summary>
    /// <param name="outcome">Why.</param>
    /// <returns>The choice.</returns>
    public static LayoutMigrationChoice Refused(LayoutMigrationChoiceOutcome outcome) => new(outcome, Task.CompletedTask);
}
