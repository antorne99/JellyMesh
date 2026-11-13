using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The released libraries of removed servers to take off the page (§10 row 281).
/// </summary>
/// <param name="Libraries">The libraries, as the status lists them.</param>
public sealed record HideReleasedLibrariesRequest([Required] IReadOnlyList<ReleasedLibrary> Libraries);
