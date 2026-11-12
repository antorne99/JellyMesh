using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyMesh.Activity;

/// <summary>
/// Writes entries to the activity log of the server. Never throws: a log that cannot be written must not stop the work.
/// </summary>
public interface IActivityWriter
{
    /// <summary>
    /// Writes one entry.
    /// </summary>
    /// <param name="entry">The entry. Holds no address or token.</param>
    /// <returns>A task that ends when the entry is written or given up.</returns>
    Task WriteAsync(ActivityEntry entry);
}
