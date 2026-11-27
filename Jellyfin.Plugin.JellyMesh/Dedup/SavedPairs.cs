using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Dedup;

/// <summary>
/// The saved titles of a type with their current copies.
/// </summary>
/// <typeparam name="T">The type of the titles.</typeparam>
/// <param name="Pairs">Each saved title with every current copy of it.</param>
/// <param name="Left">How many saved titles have too many copies to pair (row 542).</param>
public sealed record SavedPairs<T>(IReadOnlyList<(T Saved, T Current)> Pairs, int Left)
    where T : IServerItem;
