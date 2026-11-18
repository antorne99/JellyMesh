using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// A failure as shown on the configuration page.
/// </summary>
/// <param name="Failure">The failure.</param>
/// <param name="Message">The text to show.</param>
public sealed record FailureResponse(RemoteFailure Failure, string Message)
{
    /// <summary>
    /// Gets the name of the configured server a refused add or edit matches, or that of the other server at an edited address.
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>
    /// Gets the field of the edit form a refusal is about, null when it is about none.
    /// </summary>
    public RemoteEditField? Field { get; init; }
}
