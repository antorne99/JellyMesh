using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;

namespace Jellyfin.Plugin.JellyMesh.Configuration;

/// <summary>
/// The rows of <see cref="ConfigurationTables"/> for one <see cref="PluginConfiguration"/>, as SQLite gives them back:
/// text as <see cref="string"/>, integers and flags as <see cref="long"/>.
/// </summary>
internal sealed class ConfigurationRows
{
    private const string ImportedLibrary = "imported_library";
    private const string PreexistingLibrary = "preexisting_library";
    private const string FollowedLibrary = "followed_library";
    private const string ReleasedLibrary = "released_library";
    private const string HiddenReleasedLibrary = "hidden_released_library";
    private const string CreatedLibrary = "created_library";

    // Imported libraries absent from one answer, and those gone from two, by id and name at the same positions (§10 row 436).
    private const string AbsentLibrary = "absent_library";
    private const string VanishedLibrary = "vanished_library";
    private const string VanishedLibraryName = "vanished_library_name";

    // The users who lost titles in the last mode change, by library and user name at the same positions (§10 row 438).
    private const string LostAccessLibrary = "lost_access_library";
    private const string LostAccessUser = "lost_access_user";

    // The positions of the adopted libraries in the list of a server, so no schema step is needed (§10 row 441).
    private const string AdoptedLibrary = "adopted_library";

    private static readonly Setting[] _settings =
    [
        Number(nameof(PluginConfiguration.SyncIntervalSeconds), config => config.SyncIntervalSeconds, (config, value) => config.SyncIntervalSeconds = value),
        Text(nameof(PluginConfiguration.ReconciliationTime), config => config.ReconciliationTime, (config, value) => config.ReconciliationTime = value),
        Text(nameof(PluginConfiguration.FederatedFolderPath), config => config.FederatedFolderPath, (config, value) => config.FederatedFolderPath = value),
        Flag(nameof(PluginConfiguration.MetadataProtection), config => config.MetadataProtection, (config, value) => config.MetadataProtection = value),
        Flag(nameof(PluginConfiguration.FederateCollections), config => config.FederateCollections, (config, value) => config.FederateCollections = value),
        Flag(nameof(PluginConfiguration.CloseTranscodeAtSlotExpiry), config => config.CloseTranscodeAtSlotExpiry, (config, value) => config.CloseTranscodeAtSlotExpiry = value),
        Number(nameof(PluginConfiguration.RemotePlaybackInfoTimeoutSeconds), config => config.RemotePlaybackInfoTimeoutSeconds, (config, value) => config.RemotePlaybackInfoTimeoutSeconds = value),
        Number(nameof(PluginConfiguration.StreamOpenTimeoutSeconds), config => config.StreamOpenTimeoutSeconds, (config, value) => config.StreamOpenTimeoutSeconds = value),
        Number(nameof(PluginConfiguration.PlaybackIdleSeconds), config => config.PlaybackIdleSeconds, (config, value) => config.PlaybackIdleSeconds = value),
        Number(nameof(PluginConfiguration.SessionRetentionHours), config => config.SessionRetentionHours, (config, value) => config.SessionRetentionHours = value),
        Number(nameof(PluginConfiguration.PolicyCacheMinutes), config => config.PolicyCacheMinutes, (config, value) => config.PolicyCacheMinutes = value),
        Text(
            nameof(PluginConfiguration.NewServerLibraryMode),
            config => config.NewServerLibraryMode.ToString(),
            (config, value) => config.NewServerLibraryMode = Enum.TryParse<LibraryMode>(value, out var mode) && Enum.IsDefined(mode) ? mode : config.NewServerLibraryMode)
    ];

    private readonly IReadOnlyDictionary<string, IReadOnlyList<object[]>> _tables;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationRows"/> class.
    /// </summary>
    /// <param name="tables">The rows of each table; a table left out has none.</param>
    public ConfigurationRows(IReadOnlyDictionary<string, IReadOnlyList<object[]>> tables)
    {
        _tables = tables;
    }

    /// <summary>
    /// Gets the rows of a database with no configuration saved.
    /// </summary>
    public static ConfigurationRows None { get; } = new(new Dictionary<string, IReadOnlyList<object[]>>(StringComparer.Ordinal));

    /// <summary>
    /// Turns a configuration into rows. A server, removed server, placed server or peer listed twice keeps its first entry.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>Its rows.</returns>
    public static ConfigurationRows From(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var settings = _settings.Select(setting => Row(setting.Name, setting.Get(config))).ToList();

        var servers = new List<object[]>();
        var serverValues = new List<object[]>();
        var automatic = new List<object[]>();
        var remoteLibraries = new List<object[]>();
        var pendingLibraries = new List<object[]>();
        foreach (var (server, position) in config.Servers.DistinctBy(server => server.Id).Select((server, position) => (server, position)))
        {
            var id = Id(server.Id);
            servers.Add(Row(id, position, server.JellyfinServerId, server.Name, server.Url, server.EncryptedCredentials, server.Mode.ToString(), server.ProtocolVersion, server.AutomaticLibraries, server.FollowSkipped, server.RenameUnfinished, server.LibraryMode.ToString()));
            serverValues.AddRange(Values(id, ImportedLibrary, server.ImportedLibraryIds));
            serverValues.AddRange(Values(id, PreexistingLibrary, server.PreexistingLibraryIds));
            serverValues.AddRange(Values(id, FollowedLibrary, server.FollowedLibraryIds));
            serverValues.AddRange(Values(id, ReleasedLibrary, server.ReleasedAutomaticLibraries));
            serverValues.AddRange(Values(id, CreatedLibrary, server.CreatedLibraryKeys));
            automatic.AddRange(Libraries(id, server.AutomaticLibraryRegistry));
            serverValues.AddRange(Values(id, AdoptedLibrary, AdoptedPositions(server.AutomaticLibraryRegistry)));
            serverValues.AddRange(Values(id, AbsentLibrary, server.AbsentLibraryIds));
            serverValues.AddRange(Values(id, VanishedLibrary, server.VanishedLibraries.Select(library => library.Id)));
            serverValues.AddRange(Values(id, VanishedLibraryName, server.VanishedLibraries.Select(library => library.Name)));
            serverValues.AddRange(Values(id, LostAccessLibrary, server.LostAccess.Select(lost => lost.Library)));
            serverValues.AddRange(Values(id, LostAccessUser, server.LostAccess.Select(lost => lost.User)));
            remoteLibraries.AddRange(server.RemoteLibraries.Select((library, position) => Row(id, position, library.Id, library.Name, library.CollectionType)));
            pendingLibraries.AddRange(server.PendingLibraries.Select((library, position)
                => Row(id, position, library.Kind.ToString(), library.Name, library.RemoteLibraryId, JsonSerializer.Serialize(library.Paths))));
        }

        var removed = new List<object[]>();
        var removedValues = new List<object[]>();
        var removedLibraries = new List<object[]>();
        foreach (var (held, position) in config.RemovedServerLibraries.DistinctBy(held => held.ServerId).Select((held, position) => (held, position)))
        {
            var id = Id(held.ServerId);
            removed.Add(Row(id, position, held.FollowSkipped));
            removedValues.AddRange(Values(id, PreexistingLibrary, held.PreexistingLibraryIds));
            removedValues.AddRange(Values(id, FollowedLibrary, held.FollowedLibraryIds));
            removedValues.AddRange(Values(id, ReleasedLibrary, held.ReleasedLibraries));
            removedValues.AddRange(Values(id, HiddenReleasedLibrary, held.HiddenReleasedLibraries));
            removedLibraries.AddRange(Libraries(id, held.Libraries));
            removedValues.AddRange(Values(id, AdoptedLibrary, AdoptedPositions(held.Libraries)));
        }

        var migration = config.LayoutMigration;
        var peers = config.Peers.DistinctBy(peer => peer.UserId).ToList();
        return new ConfigurationRows(new Dictionary<string, IReadOnlyList<object[]>>(StringComparer.Ordinal)
        {
            [ConfigurationTables.Settings] = settings,
            [ConfigurationTables.Servers] = servers,
            [ConfigurationTables.ServerValues] = serverValues,
            [ConfigurationTables.ServerAutomaticLibraries] = automatic,
            [ConfigurationTables.ServerRemoteLibraries] = remoteLibraries,
            [ConfigurationTables.ServerPendingLibraries] = pendingLibraries,
            [ConfigurationTables.RemovedServers] = removed,
            [ConfigurationTables.RemovedServerValues] = removedValues,
            [ConfigurationTables.RemovedServerLibraries] = removedLibraries,
            [ConfigurationTables.LayoutMigration] = [Row(1, migration.Mode.ToString(), migration.Step.ToString(), migration.Error.ToString(), migration.ErrorLibrary)],
            [ConfigurationTables.LayoutMigrationPlacedServers] = migration.PlacedServers.Distinct().Select((server, position) => Row(Id(server), position)).ToList(),
            [ConfigurationTables.Peers] = peers.Select((peer, position) => Row(Id(peer.UserId), position, peer.MaxStreams)).ToList(),
            [ConfigurationTables.PeerSharedLibraries] = peers.SelectMany(peer => peer.SharedLibraryIds.Select((library, position) => Row(Id(peer.UserId), position, library))).ToList()
        });
    }

    /// <summary>
    /// Gets the rows of a table.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <returns>Its rows.</returns>
    public IReadOnlyList<object[]> Rows(string table) => _tables.TryGetValue(table, out var rows) ? rows : [];

    /// <summary>
    /// Tells whether a table has the same rows here and in <paramref name="other"/>, in any order.
    /// </summary>
    /// <param name="other">The other rows.</param>
    /// <param name="table">The table.</param>
    /// <returns>Whether nothing in the table changed.</returns>
    public bool SameRows(ConfigurationRows other, string table)
    {
        ArgumentNullException.ThrowIfNull(other);
        var mine = Rows(table);
        var theirs = other.Rows(table);
        return mine.Count == theirs.Count
            && mine.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(theirs.Select(Key).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>
    /// Tells whether a credential saved in these rows is replaced or gone in <paramref name="next"/>.
    /// </summary>
    /// <param name="next">The rows that follow these.</param>
    /// <returns>Whether some ciphertext of these rows is no longer in use.</returns>
    public bool DropsCredentialsIn(ConfigurationRows next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var kept = next.Rows(ConfigurationTables.Servers).Select(Credentials).ToHashSet();
        return Rows(ConfigurationTables.Servers).Select(Credentials).Any(entry => entry.Ciphertext.Length > 0 && !kept.Contains(entry));
    }

    /// <summary>
    /// Tells whether any table differs from <paramref name="other"/>.
    /// </summary>
    /// <param name="other">The other rows.</param>
    /// <returns>Whether something changed.</returns>
    public bool DiffersFrom(ConfigurationRows other) => ConfigurationTables.All.Any(table => !SameRows(other, table.Name));

    /// <summary>
    /// Builds the configuration these rows hold. A missing or unreadable setting keeps its default.
    /// </summary>
    /// <returns>A new configuration.</returns>
    public PluginConfiguration ToConfiguration()
    {
        var config = new PluginConfiguration();
        var saved = Rows(ConfigurationTables.Settings).ToDictionary(row => (string)row[0], row => Convert.ToString(row[1], CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.Ordinal);
        foreach (var setting in _settings)
        {
            if (saved.TryGetValue(setting.Name, out var value))
            {
                setting.Set(config, value);
            }
        }

        var serverValues = Lists(ConfigurationTables.ServerValues);
        var automatic = LibrariesOf(ConfigurationTables.ServerAutomaticLibraries);
        var remote = LibrariesOf(ConfigurationTables.ServerRemoteLibraries);
        var pending = LibrariesOf(ConfigurationTables.ServerPendingLibraries);
        foreach (var row in Rows(ConfigurationTables.Servers).OrderBy(row => (long)row[1]))
        {
            var id = (string)row[0];
            var server = new RemoteServerConfig
            {
                Id = Guid.Parse(id),
                JellyfinServerId = (string)row[2],
                Name = (string)row[3],
                Url = (string)row[4],
                EncryptedCredentials = (string)row[5],
                Mode = Enum.TryParse<RemoteMode>((string)row[6], out var mode) ? mode : RemoteMode.Unknown,
                ProtocolVersion = (int)(long)row[7],
                LibraryMode = Enum.TryParse<LibraryMode>((string)row[11], out var libraryMode) && Enum.IsDefined(libraryMode)
                    ? libraryMode
                    : (long)row[8] != 0 ? LibraryMode.ByType : LibraryMode.None,
                FollowSkipped = (long)row[9] != 0,
                RenameUnfinished = (long)row[10] != 0
            };
            Fill(server.ImportedLibraryIds, serverValues, id, ImportedLibrary);
            Fill(server.PreexistingLibraryIds, serverValues, id, PreexistingLibrary);
            Fill(server.FollowedLibraryIds, serverValues, id, FollowedLibrary);
            Fill(server.ReleasedAutomaticLibraries, serverValues, id, ReleasedLibrary);
            Fill(server.CreatedLibraryKeys, serverValues, id, CreatedLibrary);
            Fill(server.AutomaticLibraryRegistry, automatic, id);
            MarkAdopted(server.AutomaticLibraryRegistry, serverValues, id);
            Fill(server.AbsentLibraryIds, serverValues, id, AbsentLibrary);
            FillVanished(server.VanishedLibraries, serverValues, id);
            FillLostAccess(server.LostAccess, serverValues, id);

            // A library kept by an older version, or by its imported files, counts as made, so one deleted is never made again.
            foreach (var key in server.AutomaticLibraryRegistry.Select(record => record.KeyOf()).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (!server.CreatedLibraryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    server.CreatedLibraryKeys.Add(key);
                }
            }

            foreach (var library in remote[id].OrderBy(library => (long)library[1]))
            {
                server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = (string)library[2], Name = (string)library[3], CollectionType = (string)library[4] });
            }

            foreach (var library in pending[id].OrderBy(library => (long)library[1]))
            {
                server.PendingLibraries.Add(Pending(library));
            }

            config.Servers.Add(server);
        }

        var removedValues = Lists(ConfigurationTables.RemovedServerValues);
        var removedLibraries = LibrariesOf(ConfigurationTables.RemovedServerLibraries);
        foreach (var row in Rows(ConfigurationTables.RemovedServers).OrderBy(row => (long)row[1]))
        {
            var id = (string)row[0];
            var held = new RemovedServerLibrariesConfig { ServerId = Guid.Parse(id), FollowSkipped = (long)row[2] != 0 };
            Fill(held.PreexistingLibraryIds, removedValues, id, PreexistingLibrary);
            Fill(held.FollowedLibraryIds, removedValues, id, FollowedLibrary);
            Fill(held.ReleasedLibraries, removedValues, id, ReleasedLibrary);
            Fill(held.HiddenReleasedLibraries, removedValues, id, HiddenReleasedLibrary);
            Fill(held.Libraries, removedLibraries, id);
            MarkAdopted(held.Libraries, removedValues, id);
            config.RemovedServerLibraries.Add(held);
        }

        if (Rows(ConfigurationTables.LayoutMigration) is [var migration, ..])
        {
            config.LayoutMigration.Mode = Enum.TryParse<LayoutMigrationMode>((string)migration[1], out var migrationMode) ? migrationMode : default;
            config.LayoutMigration.Step = Enum.TryParse<AutomaticMigrationStep>((string)migration[2], out var step) ? step : default;
            config.LayoutMigration.Error = Enum.TryParse<AutomaticMigrationError>((string)migration[3], out var error) ? error : default;
            config.LayoutMigration.ErrorLibrary = (string)migration[4];
        }

        foreach (var row in Rows(ConfigurationTables.LayoutMigrationPlacedServers).OrderBy(row => (long)row[1]))
        {
            config.LayoutMigration.PlacedServers.Add(Guid.Parse((string)row[0]));
        }

        var shared = Rows(ConfigurationTables.PeerSharedLibraries).ToLookup(row => (string)row[0], StringComparer.Ordinal);
        foreach (var row in Rows(ConfigurationTables.Peers).OrderBy(row => (long)row[1]))
        {
            var peer = new ProviderPeerConfig { UserId = Guid.Parse((string)row[0]), MaxStreams = (int)(long)row[2] };
            foreach (var library in shared[(string)row[0]].OrderBy(library => (long)library[1]))
            {
                peer.SharedLibraryIds.Add((string)library[2]);
            }

            config.Peers.Add(peer);
        }

        return config;
    }

    // A library asked of the server; paths that do not read back leave it with none, so it matches no library.
    private static PendingLibraryConfig Pending(object[] row)
    {
        var library = new PendingLibraryConfig
        {
            Kind = Enum.TryParse<ServerLibraryKind>((string)row[2], out var kind) ? kind : default,
            Name = (string)row[3],
            RemoteLibraryId = (string)row[4]
        };
        try
        {
            foreach (var path in (JsonSerializer.Deserialize<string?[]>((string)row[5]) ?? []).OfType<string>())
            {
                library.Paths.Add(path);
            }
        }
        catch (JsonException)
        {
            library.Paths.Clear();
        }

        return library;
    }

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    // Integers and flags become long and text stays string, as SQLite reads them back, so rows compare equal after a load.
    private static object[] Row(params object[] values) => values
        .Select(value => value switch
        {
            int number => (long)number,
            bool flag => flag ? 1L : 0L,
            _ => value
        })
        .ToArray();

    private static IEnumerable<object[]> Values(string owner, string list, IEnumerable<string> values)
        => values.Select((value, position) => Row(owner, list, position, value));

    private static IEnumerable<object[]> Libraries(string owner, IEnumerable<AutomaticLibraryConfig> libraries)
        => libraries.Select((library, position) => Row(owner, position, library.Kind.ToString(), library.Name, library.ItemId, library.RemoteLibraryId));

    private static void Fill(Collection<string> target, ILookup<(string Owner, string List), object[]> lists, string owner, string list)
    {
        foreach (var row in lists[(owner, list)].OrderBy(row => (long)row[2]))
        {
            target.Add((string)row[3]);
        }
    }

    // Pairs each vanished id with the name at its position; one with no name keeps an empty one.
    private static void FillVanished(Collection<VanishedLibraryConfig> target, ILookup<(string Owner, string List), object[]> lists, string owner)
    {
        var names = lists[(owner, VanishedLibraryName)].ToDictionary(row => (long)row[2], row => (string)row[3]);
        foreach (var row in lists[(owner, VanishedLibrary)].OrderBy(row => (long)row[2]))
        {
            target.Add(new VanishedLibraryConfig { Id = (string)row[3], Name = names.GetValueOrDefault((long)row[2], string.Empty) });
        }
    }

    private static void FillLostAccess(Collection<LostAccessConfig> target, ILookup<(string Owner, string List), object[]> lists, string owner)
    {
        var users = lists[(owner, LostAccessUser)].ToDictionary(row => (long)row[2], row => (string)row[3]);
        foreach (var row in lists[(owner, LostAccessLibrary)].OrderBy(row => (long)row[2]))
        {
            if (users.TryGetValue((long)row[2], out var user))
            {
                target.Add(new LostAccessConfig { Library = (string)row[3], User = user });
            }
        }
    }

    private static IEnumerable<string> AdoptedPositions(IEnumerable<AutomaticLibraryConfig> libraries)
        => libraries.Select((library, position) => (library.Adopted, position)).Where(pair => pair.Adopted).Select(pair => pair.position.ToString(CultureInfo.InvariantCulture));

    // Marks as adopted the libraries at the positions kept for them, read in the order of their positions.
    private static void MarkAdopted(Collection<AutomaticLibraryConfig> target, ILookup<(string Owner, string List), object[]> lists, string owner)
    {
        foreach (var row in lists[(owner, AdoptedLibrary)])
        {
            if (int.TryParse((string)row[3], NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position < target.Count)
            {
                target[position].Adopted = true;
            }
        }
    }

    private static void Fill(Collection<AutomaticLibraryConfig> target, ILookup<string, object[]> libraries, string owner)
    {
        foreach (var row in libraries[owner].OrderBy(row => (long)row[1]))
        {
            target.Add(new AutomaticLibraryConfig
            {
                Kind = Enum.TryParse<ServerLibraryKind>((string)row[2], out var kind) ? kind : default,
                Name = (string)row[3],
                ItemId = (string)row[4],
                RemoteLibraryId = (string)row[5]
            });
        }
    }

    // Each value tagged with its type, so a text "1" and a number 1 are different rows.
    // The server id and ciphertext of a row of the servers table.
    private static (string Id, string Ciphertext) Credentials(object[] row) => ((string)row[0], (string)row[5]);

    private static string Key(object[] row) => string.Join('\u001f', row.Select(value => value switch
    {
        long number => "n" + number.ToString(CultureInfo.InvariantCulture),
        string text => "t" + text,
        _ => "o" + Convert.ToString(value, CultureInfo.InvariantCulture)
    }));

    private static Setting Number(string name, Func<PluginConfiguration, int> get, Action<PluginConfiguration, int> set) => new(
        name,
        config => get(config).ToString(CultureInfo.InvariantCulture),
        (config, value) =>
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                set(config, number);
            }
        });

    private static Setting Flag(string name, Func<PluginConfiguration, bool> get, Action<PluginConfiguration, bool> set) => new(
        name,
        config => get(config) ? "true" : "false",
        (config, value) =>
        {
            if (bool.TryParse(value, out var flag))
            {
                set(config, flag);
            }
        });

    private static Setting Text(string name, Func<PluginConfiguration, string> get, Action<PluginConfiguration, string> set) => new(name, get, set);

    private ILookup<(string Owner, string List), object[]> Lists(string table)
        => Rows(table).ToLookup(row => ((string)row[0], (string)row[1]));

    private ILookup<string, object[]> LibrariesOf(string table)
        => Rows(table).ToLookup(row => (string)row[0], StringComparer.Ordinal);

    private sealed record Setting(string Name, Func<PluginConfiguration, string> Get, Action<PluginConfiguration, string> Set);
}
