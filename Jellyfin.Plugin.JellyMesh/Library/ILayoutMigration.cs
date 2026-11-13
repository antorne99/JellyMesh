using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The migration from the flat layout to one folder per server, manual or automatic as the admin chooses (Q40, Q41,
/// Q47-Q49, Q55-Q57, D12).
/// </summary>
/// <remarks>
/// It writes the rows of every user, so it runs from the system or from an admin request, never from a user request:
/// the admin acts as admin, and decision 96 covers users (§10 row 372).
/// </remarks>
public interface ILayoutMigration
{
    /// <summary>
    /// Once every library on an old path holds the new paths of the same kind, and each server folder is in some library,
    /// counting only the kinds with old titles, carries each user's state from the old stubs to the new items, and empties
    /// the old folders when every old title is done. Without the new paths, or any server, it does nothing (Q49, Q215, Q249, Q260).
    /// Before the admin chooses it does nothing; in automatic mode it carries at that step and resumes the other steps (Q55-Q57).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records the admin's choice in the banner. Manual runs the manual migration; automatic starts the automatic one, or
    /// resumes it from its saved step after an error (Q55, Q57). From automatic there is no way back to manual.
    /// </summary>
    /// <param name="mode">The mode chosen.</param>
    /// <returns>What the choice did.</returns>
    LayoutMigrationChoice Choose(LayoutMigrationMode mode);

    /// <summary>
    /// Gets where the migration stands, for the banner of the page.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <returns>The status, null when there is nothing to migrate and no library on an old path.</returns>
    LayoutMigrationStatus? GetStatus(string root);
}
