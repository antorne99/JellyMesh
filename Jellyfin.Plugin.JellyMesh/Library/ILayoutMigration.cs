using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The manual migration from the flat layout to one folder per server (Q40, Q41, Q47-Q49, D12).
/// </summary>
/// <remarks>
/// It writes the rows of every user, so only the system may run it, never a user request (decision 96).
/// </remarks>
public interface ILayoutMigration
{
    /// <summary>
    /// Once every library on an old path holds the new paths of the same kind, and each server folder is in some library,
    /// carries each user's state from the old stubs to the new items, and empties the old folders when every old title is
    /// done. Without the new paths it does nothing (Q49, Q215).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets where the migration stands, for the banner of the page.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <returns>The status, null when there is nothing to migrate and no library on an old path.</returns>
    LayoutMigrationStatus? GetStatus(string root);
}
