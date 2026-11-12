namespace Jellyfin.Plugin.JellyMesh.Activity;

/// <summary>
/// Writes entries to the activity log of the server. Never throws: a log that cannot be written must not stop the work.
/// </summary>
public interface IActivityWriter
{
    /// <summary>
    /// Queues one entry, in order, without waiting for it to be written.
    /// </summary>
    /// <param name="entry">The entry. Holds no address or token.</param>
    void Write(ActivityEntry entry);
}
