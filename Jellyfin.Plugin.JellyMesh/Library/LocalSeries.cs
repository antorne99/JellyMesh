using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A series of the local library that a stub series has to merge with (decisions 2 and 3).
/// </summary>
/// <param name="Name">The exact name.</param>
/// <param name="Year">The year.</param>
/// <param name="Overview">The plot.</param>
/// <param name="ProviderIds">The provider IDs.</param>
/// <param name="PosterPath">The path of the local poster, if it has one.</param>
/// <param name="BackdropPath">The path of the local backdrop, if it has one.</param>
public sealed record LocalSeries(
    string? Name,
    int? Year,
    string? Overview,
    IReadOnlyDictionary<string, string> ProviderIds,
    string? PosterPath,
    string? BackdropPath);
