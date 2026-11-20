using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A library on an old path of the flat layout or of the 0.5 server folders.
/// </summary>
/// <param name="Name">The library.</param>
/// <param name="OldPaths">The old paths it holds.</param>
/// <param name="MissingPaths">The paths of the type folders of the same kind it still lacks, for the kinds with old titles (Q249).</param>
public sealed record LayoutMigrationLibrary(string Name, IReadOnlyList<string> OldPaths, IReadOnlyList<string> MissingPaths)
{
    /// <summary>
    /// Gets the type folders it lacks for every old path it holds, also of kinds with no old titles, to add before removing them.
    /// </summary>
    public IReadOnlyList<string> PathsToAdd { get; init; } = [];
}
