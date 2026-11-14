using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Keeps the watch state of the titles of a removed server and puts it back when the same content appears again, from
/// that server added back or from another one (row 412).
/// </summary>
public interface IWatchStateKeeper
{
    /// <summary>
    /// Saves each user's watch state of the stubs and series about to be removed, by the identity of their content.
    /// </summary>
    /// <param name="root">The federated folder.</param>
    /// <param name="stubs">The stubs of removed servers about to be removed.</param>
    /// <param name="series">The series of removed servers that may be removed.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the state is saved, so that the stubs may go.</returns>
    Task<bool> KeepAsync(string root, IReadOnlyCollection<StubEntry> stubs, IReadOnlyCollection<SeriesStub> series, CancellationToken cancellationToken);

    /// <summary>
    /// Puts the saved state back on the stubs of the same content, with the rules of row 2, and forgets what it put back.
    /// Records unused for 90 days go.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the state is back.</returns>
    Task RestoreAsync(CancellationToken cancellationToken);
}
