using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Keeps the federated folder in step with the resolved catalog (plan §3.1).
/// </summary>
public interface ILibraryMaterializer
{
    /// <summary>
    /// Gets the outcome of the last run, null if none ran since start.
    /// </summary>
    MaterializationReport? LastReport { get; }

    /// <summary>
    /// Writes what is missing, updates what changed and removes what is proved gone from the remotes.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<MaterializationReport> MaterializeAsync(CancellationToken cancellationToken);
}
