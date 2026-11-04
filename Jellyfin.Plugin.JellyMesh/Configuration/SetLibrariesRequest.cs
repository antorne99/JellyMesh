using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The libraries to import from a server.
/// </summary>
/// <param name="LibraryIds">The remote library identifiers.</param>
public sealed record SetLibrariesRequest([Required] IReadOnlyList<string> LibraryIds);
