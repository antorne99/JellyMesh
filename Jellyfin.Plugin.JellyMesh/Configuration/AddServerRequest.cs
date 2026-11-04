using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// Request to add a remote server. A class rather than a record, so that no generated
/// <see cref="object.ToString"/> can print the password.
/// </summary>
public sealed class AddServerRequest
{
    /// <summary>
    /// Gets the display name.
    /// </summary>
    [Required]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the tailnet base URL.
    /// </summary>
    [Required]
    public string Url { get; init; } = string.Empty;

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
