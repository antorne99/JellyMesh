using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Request to enter the dedicated user of a server again. A class rather than a record, so that no generated
/// <see cref="object.ToString"/> can print the password.
/// </summary>
public sealed class CredentialsRequest
{
    /// <summary>
    /// Gets the dedicated user name.
    /// </summary>
    [Required]
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Gets the dedicated user password. May be empty on a remote, it is never echoed back.
    /// </summary>
    public string Password { get; init; } = string.Empty;
}
