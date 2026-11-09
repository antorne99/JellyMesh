namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// State of the secret key. Anything but <see cref="Ok"/> suspends sync and remote playback.
/// </summary>
public enum KeyState
{
    /// <summary>
    /// The key is usable and decrypts every stored credential.
    /// </summary>
    Ok,

    /// <summary>
    /// There is no key: the key file cannot be kept in the plugin data folder.
    /// </summary>
    Missing,

    /// <summary>
    /// A stored credential does not decrypt: the key file was lost or replaced, or the stored value was altered.
    /// </summary>
    Wrong
}
