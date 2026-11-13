using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// A library on an old path of the flat layout.
/// </summary>
/// <param name="Name">The library.</param>
/// <param name="OldPaths">The old paths it holds.</param>
/// <param name="MissingPaths">The paths of the server folders of the same kind it still lacks, for the kinds with old titles (Q249).</param>
public sealed record LayoutMigrationLibrary(string Name, IReadOnlyList<string> OldPaths, IReadOnlyList<string> MissingPaths);
