using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// The schema steps of the plugin database, in order; later phases append theirs.
/// </summary>
public static class SchemaMigrations
{
    /// <summary>
    /// Gets every step, with versions 1, 2, 3 and so on.
    /// </summary>
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        // The first version only marks the file as the plugin database; the tables come with the phases that fill them.
        new(1, static (_, _) => { })
    ];

    /// <summary>
    /// Gets the newest schema version this build knows.
    /// </summary>
    public static int Latest => All[^1].Version;
}
