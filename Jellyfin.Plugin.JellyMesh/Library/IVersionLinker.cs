using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Links the copies of a title as versions of one item.
/// </summary>
public interface IVersionLinker
{
    /// <summary>
    /// Links every group of versions as the registry has it, and saves nothing that is already linked.
    /// </summary>
    /// <param name="thorough">Whether to look up by path a stub whose item is not where its path says.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task LinkAsync(bool thorough, CancellationToken cancellationToken);
}
