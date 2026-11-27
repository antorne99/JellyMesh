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

        // The mark of the migration of the 0.4 and 0.5 data (F32).
        new(LegacyImport.SchemaVersion, LegacyImport.CreateTable),

        // The library modes (F39).
        new(6, ConfigurationTables.AddLibraryModes),

        // The warnings of the sync history (F38).
        new(7, SmallStateSchema.AddWarnings),

        // The stubs that split and wait for their watch state (F44b).
        new(8, Library.StubTables.AddSplits),

        // The operation of each watch state carry (row 575).
        new(9, SmallStateSchema.AddCarryOperations)
    ];

    /// <summary>
    /// Gets the newest schema version this build knows.
    /// </summary>
    public static int Latest => All[^1].Version;
}
