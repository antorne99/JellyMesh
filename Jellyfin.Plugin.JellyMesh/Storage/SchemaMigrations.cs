using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Configuration;

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
        new(1, static (_, _) => { }),

        // The federation index and the stub registry (F28).
        new(2, static (connection, transaction) =>
        {
            Index.IndexTables.Create(connection, transaction);
            Library.StubTables.Create(connection, transaction);
        }),
        new(SmallStateSchema.Version, SmallStateSchema.Apply),
        new(4, ConfigurationTables.Create),

        // Reserved for the data migration of F32, which takes step 5; the steps are renumbered when the phases merge.
        new(5, static (_, _) => { }),

        // The library modes (F39).
        new(6, ConfigurationTables.AddLibraryModes)
    ];

    /// <summary>
    /// Gets the newest schema version this build knows.
    /// </summary>
    public static int Latest => All[^1].Version;
}
