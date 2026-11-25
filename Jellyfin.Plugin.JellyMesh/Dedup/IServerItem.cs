namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// An item of a server, as a rule of identity reads it.
/// </summary>
public interface IServerItem
{
    /// <summary>Gets the plugin's ID for the server holding the item.</summary>
    string ServerId { get; }

    /// <summary>Gets the ID of the item on that server.</summary>
    string ItemId { get; }
}
