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
    /// The key is not set.
    /// </summary>
    Missing,

    /// <summary>
    /// The key is set but shorter than <see cref="CredentialCipher.MinimumKeyLength"/>.
    /// </summary>
    TooShort,

    /// <summary>
    /// A stored credential does not decrypt: the key changed, or the stored value was altered.
    /// </summary>
    Wrong
}
