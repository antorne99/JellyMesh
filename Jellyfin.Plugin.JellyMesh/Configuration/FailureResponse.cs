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
public sealed record FailureResponse(RemoteFailure Failure, string Message);
