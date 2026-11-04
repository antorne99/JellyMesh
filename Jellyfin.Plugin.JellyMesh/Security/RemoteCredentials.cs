namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Login of the dedicated non-admin user on a remote. A class rather than a record, so that no
/// generated <see cref="object.ToString"/> can print the password.
/// </summary>
public sealed class RemoteCredentials
{
    /// <summary>
    /// Gets the user name.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Gets the password.
    /// </summary>
    public required string Password { get; init; }
}
