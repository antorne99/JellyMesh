namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The stubs written in the federated folder, by their stable ID. Reads need no lock, and they survive a stale or lost
/// index, because the sources of a stub are stored with it.
/// </summary>
public interface IStubRegistry
{
    /// <summary>
    /// Finds a stub by ID, the last segment of the URL in its <c>.strm</c>.
    /// </summary>
    /// <param name="stubId">The stub.</param>
    /// <returns>The stub, null if there is none.</returns>
    StubEntry? Find(string stubId);

    /// <summary>
    /// Finds a stub by the path of its <c>.strm</c>.
    /// </summary>
    /// <param name="path">The file path, <c>BaseItem.Path</c>.</param>
    /// <returns>The stub, null for anything that is not one.</returns>
    StubEntry? FindByPath(string? path);
}
