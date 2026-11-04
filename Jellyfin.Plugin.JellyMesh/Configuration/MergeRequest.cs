using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The film titles to merge.
/// </summary>
/// <param name="WorkKeys">The titles, as the list gives them.</param>
public sealed record MergeRequest([Required] IReadOnlyList<string> WorkKeys);
