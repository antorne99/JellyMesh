using System.Collections.Generic;
using Jellyfin.Plugin.JellyMesh.Configuration;

namespace Jellyfin.Plugin.JellyMesh.Storage;

/// <summary>
/// The schema steps of the plugin database, in order; later phases append theirs.
/// </summary>
public static class SchemaMigrations
{
    /// <summary>
    /// The version from which the index may hold music, home videos, photos and books, which an older build cannot read.
    /// </summary>
    public const int NewItemKindsVersion = 9;

    /// <summary>
    /// The version from which the kept watch state may hold tracks, audiobooks, books and home videos (row 532).
    /// </summary>
    public const int KeptTypesVersion = 11;

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

        // No table changes: the index entries of the new kinds are JSON in the same rows (F50). The version keeps an older
        // build, which would take them for damage and empty the index, away from the file.
        new(NewItemKindsVersion, static (_, _) => { }),

        // The artists of a kept music video (F53).
        new(10, SmallStateSchema.AddKeptArtists),

        // The kinds of their own in the kept watch state (row 532).
        new(KeptTypesVersion, SmallStateSchema.AddKeptTypes)
    ];

    /// <summary>
    /// Gets the newest schema version this build knows.
    /// </summary>
    public static int Latest => All[^1].Version;
}
