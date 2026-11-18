using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

/// <summary>The configuration kept in the plugin database, behind <see cref="IPluginConfigurationStore"/>.</summary>
public sealed class PluginConfigurationStoreTests : IDisposable
{
    private const string Ciphertext = "djEuAAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8gISIjJCUmJygpKiss";

    private readonly List<PluginDatabase> _opened = [];

    public PluginConfigurationStoreTests() => Directory.CreateDirectory(Folder);

    // Under the temp folder, not the bind mount: WAL needs shared memory a host mount may not give.
    private string Folder { get; } = Path.Combine(Path.GetTempPath(), "jellymesh-config-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var database in _opened)
        {
            database.Dispose();
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(Folder, true);
    }

    [Fact]
    public void EverythingTheConfigurationHoldsSurvivesARestart()
    {
        var serverId = Guid.NewGuid();
        var removedId = Guid.NewGuid();
        var placedId = Guid.NewGuid();
        var peerId = Guid.NewGuid();
        Store().Update(config =>
        {
            config.SyncIntervalSeconds = 1200;
            config.ReconciliationTime = "04:15";
            config.FederatedFolderPath = "/federata";
            config.MetadataProtection = false;
            config.FederateCollections = true;
            config.CloseTranscodeAtSlotExpiry = false;
            config.RemotePlaybackInfoTimeoutSeconds = 7;
            config.StreamOpenTimeoutSeconds = 11;
            config.PlaybackIdleSeconds = 45;
            config.SessionRetentionHours = 3;
            config.PolicyCacheMinutes = 9;

            var server = new RemoteServerConfig
            {
                Id = serverId,
                JellyfinServerId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b",
                Name = "Casa",
                Url = TestValues.Url,
                EncryptedCredentials = Ciphertext,
                Mode = RemoteMode.JellyMesh,
                ProtocolVersion = 2,
                AutomaticLibraries = true,
                FollowSkipped = true,
                RenameUnfinished = true
            };
            server.ImportedLibraryIds.Add("library-2");
            server.ImportedLibraryIds.Add("library-1");
            server.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Casa - Serie", ItemId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0" });
            server.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film" });
            server.PreexistingLibraryIds.Add("9a8b7c6d5e4f30211203f4e5d6c7b8a9");
            server.FollowedLibraryIds.Add("5d6c7b8a99a8b7c6d5e4f30211203f4e");
            server.ReleasedAutomaticLibraries.Add("Casa - Vecchia");
            config.Servers.Add(server);
            config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Amici", Url = "http://friends.invalid" });

            config.HoldAutomaticLibraries(
                removedId,
                [new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Via - Film", ItemId = "1f1e2d3c4b5a69788796a5b4c3d2e1f0" }],
                ["2a8b7c6d5e4f30211203f4e5d6c7b8a9"],
                ["3d6c7b8a99a8b7c6d5e4f30211203f4e"],
                true);
            config.RemovedServerLibraries[0].ReleasedLibraries.Add("Via - Serie");
            config.RemovedServerLibraries[0].HiddenReleasedLibraries.Add("Via - Serie");

            config.LayoutMigration.Mode = LayoutMigrationMode.Automatic;
            config.LayoutMigration.Step = AutomaticMigrationStep.RemoveOldPaths;
            config.LayoutMigration.Error = AutomaticMigrationError.RemoveFailed;
            config.LayoutMigration.ErrorLibrary = "Film di casa";
            config.LayoutMigration.PlacedServers.Add(placedId);

            var peer = new ProviderPeerConfig { UserId = peerId, MaxStreams = 3 };
            peer.SharedLibraryIds.Add("shared-b");
            peer.SharedLibraryIds.Add("shared-a");
            config.Peers.Add(peer);
        });

        var loaded = Store().Read(config => config);

        Assert.Equal(
            (1200, "04:15", "/federata", false, true, false, 7, 11, 45, 3, 9),
            (loaded.SyncIntervalSeconds, loaded.ReconciliationTime, loaded.FederatedFolderPath, loaded.MetadataProtection, loaded.FederateCollections, loaded.CloseTranscodeAtSlotExpiry,
                loaded.RemotePlaybackInfoTimeoutSeconds, loaded.StreamOpenTimeoutSeconds, loaded.PlaybackIdleSeconds, loaded.SessionRetentionHours, loaded.PolicyCacheMinutes));

        Assert.Equal(["Casa", "Amici"], loaded.Servers.Select(server => server.Name));
        var casa = loaded.Servers[0];
        Assert.Equal(
            (serverId, "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b", TestValues.Url, Ciphertext, RemoteMode.JellyMesh, 2, true, true, true),
            (casa.Id, casa.JellyfinServerId, casa.Url, casa.EncryptedCredentials, casa.Mode, casa.ProtocolVersion, casa.AutomaticLibraries, casa.FollowSkipped, casa.RenameUnfinished));
        Assert.Equal(["library-2", "library-1"], casa.ImportedLibraryIds);
        Assert.Equal(
            [(ServerLibraryKind.Series, "Casa - Serie", "0f1e2d3c4b5a69788796a5b4c3d2e1f0"), (ServerLibraryKind.Movies, "Casa - Film", string.Empty)],
            casa.AutomaticLibraryRegistry.Select(library => (library.Kind, library.Name, library.ItemId)));
        Assert.Equal(["9a8b7c6d5e4f30211203f4e5d6c7b8a9"], casa.PreexistingLibraryIds);
        Assert.Equal(["5d6c7b8a99a8b7c6d5e4f30211203f4e"], casa.FollowedLibraryIds);
        Assert.Equal(["Casa - Vecchia"], casa.ReleasedAutomaticLibraries);
        Assert.Equal((string.Empty, RemoteMode.Unknown, false), (loaded.Servers[1].EncryptedCredentials, loaded.Servers[1].Mode, loaded.Servers[1].AutomaticLibraries));

        var held = Assert.Single(loaded.RemovedServerLibraries);
        Assert.Equal(removedId, held.ServerId);
        Assert.Equal((ServerLibraryKind.Movies, "Via - Film", "1f1e2d3c4b5a69788796a5b4c3d2e1f0"), Assert.Single(held.Libraries) is var library ? (library.Kind, library.Name, library.ItemId) : default);
        Assert.Equal(["2a8b7c6d5e4f30211203f4e5d6c7b8a9"], held.PreexistingLibraryIds);
        Assert.Equal(["3d6c7b8a99a8b7c6d5e4f30211203f4e"], held.FollowedLibraryIds);
        Assert.True(held.FollowSkipped);
        Assert.Equal(["Via - Serie"], held.ReleasedLibraries);
        Assert.Equal(["Via - Serie"], held.HiddenReleasedLibraries);

        Assert.Equal(
            (LayoutMigrationMode.Automatic, AutomaticMigrationStep.RemoveOldPaths, AutomaticMigrationError.RemoveFailed, "Film di casa"),
            (loaded.LayoutMigration.Mode, loaded.LayoutMigration.Step, loaded.LayoutMigration.Error, loaded.LayoutMigration.ErrorLibrary));
        Assert.Equal([placedId], loaded.LayoutMigration.PlacedServers);

        var peerLoaded = Assert.Single(loaded.Peers);
        Assert.Equal((peerId, 3), (peerLoaded.UserId, peerLoaded.MaxStreams));
        Assert.Equal(["shared-b", "shared-a"], peerLoaded.SharedLibraryIds);
    }

    [Fact]
    public void AnEmptyDatabaseGivesTheDefaults()
    {
        var loaded = Store().Read(config => config);

        Assert.Equal((900, "03:00", string.Empty, true), (loaded.SyncIntervalSeconds, loaded.ReconciliationTime, loaded.FederatedFolderPath, loaded.MetadataProtection));
        Assert.Empty(loaded.Servers);
        Assert.Empty(loaded.Peers);
    }

    [Fact]
    public void RemovedRecordsStayRemovedAfterARestart()
    {
        var first = Store();
        first.Update(config =>
        {
            config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", EncryptedCredentials = Ciphertext });
            config.Peers.Add(new ProviderPeerConfig { UserId = Guid.NewGuid(), MaxStreams = 1 });
        });

        first.Update(config =>
        {
            config.Servers.Clear();
            config.Peers.Clear();
        });

        Assert.Equal((0, 0), Store().Read(config => (config.Servers.Count, config.Peers.Count)));
    }

    [Fact]
    public void NothingButTheDatabaseIsWritten()
    {
        Store().Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", EncryptedCredentials = Ciphertext }));

        Assert.All(Directory.EnumerateFileSystemEntries(Folder), path => Assert.StartsWith(PluginDatabase.FileName, Path.GetFileName(path), StringComparison.Ordinal));
    }

    [Fact]
    public void AnUpdateThatChangesNothingDoesNotWrite()
    {
        var database = new SpyDatabase(Open());
        var store = Store(database);
        store.Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" }));
        var writes = database.Writes;

        store.Update(config => config.Servers[0].Name = "Casa");

        Assert.Equal(writes, database.Writes);
    }

    [Fact]
    public void ASettingsChangeTellsTheScheduleOnce()
    {
        var store = Store();
        var seen = new List<int>();
        store.SettingsChanged += (_, _) => seen.Add(store.Read(config => config.SyncIntervalSeconds));

        store.Update(config => config.SyncIntervalSeconds = 600);

        Assert.Equal([600], seen);
    }

    [Fact]
    public void AChangeOfTheServersOrPeersDoesNotTouchTheSchedule()
    {
        var store = Store();
        var changes = 0;
        store.SettingsChanged += (_, _) => changes++;

        store.Update(config =>
        {
            config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" });
            config.Peers.Add(new ProviderPeerConfig { UserId = Guid.NewGuid() });
        });

        Assert.Equal(0, changes);
    }

    [Fact]
    public void AnIntervalSavedBelowTheMinimumRunsAsTheMinimumWithAWarning()
    {
        Store().Update(config => config.SyncIntervalSeconds = 60);
        var logger = new CapturingLogger<PluginConfigurationStore>();

        var loaded = Store(logger: logger);

        Assert.Equal(300, loaded.Read(config => config.SyncIntervalSeconds));
        var line = Assert.Single(logger.Lines);
        Assert.Contains("SyncIntervalSeconds", line, StringComparison.Ordinal);
        Assert.Contains("60", line, StringComparison.Ordinal);
        Assert.Contains("300", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleTimeSavedAboveTheLimitRunsAsTheLimitWithAWarning()
    {
        Store().Update(config => config.PlaybackIdleSeconds = 200);
        var logger = new CapturingLogger<PluginConfigurationStore>();

        var loaded = Store(logger: logger);

        Assert.Equal(109, loaded.Read(config => config.PlaybackIdleSeconds));
        Assert.Contains("PlaybackIdleSeconds", Assert.Single(logger.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void AValidConfigurationLoadsWithoutAWarning()
    {
        Store().Update(config => config.SyncIntervalSeconds = 600);
        var logger = new CapturingLogger<PluginConfigurationStore>();

        Store(logger: logger).Read(config => config.SyncIntervalSeconds);

        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void TheVariableSetsTheFolderAndSavesIt()
    {
        var logger = new CapturingLogger<PluginConfigurationStore>();

        Assert.Equal("/federata", Store(root: " /federata ", logger: logger).Read(config => config.FederatedFolderPath));

        Assert.Equal("/federata", Store().Read(config => config.FederatedFolderPath));
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void TheVariableWinsOverASavedFolder()
    {
        Store().Update(config => config.FederatedFolderPath = "/chosen");

        Assert.Equal("/federata", Store(root: "/federata").Read(config => config.FederatedFolderPath));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/")]
    [InlineData("/films/../other")]
    public void AnUnusableVariableIsIgnoredWithAWarning(string value)
    {
        Store().Update(config => config.FederatedFolderPath = "/chosen");
        var logger = new CapturingLogger<PluginConfigurationStore>();

        var loaded = Store(root: value, logger: logger);

        Assert.Equal("/chosen", loaded.Read(config => config.FederatedFolderPath));
        Assert.Contains(EnvironmentFederatedRootSource.VariableName, Assert.Single(logger.Lines), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutTheVariableTheFolderStaysTheChoiceOfTheUser(string? value)
    {
        Store().Update(config => config.FederatedFolderPath = "/chosen");
        var logger = new CapturingLogger<PluginConfigurationStore>();

        var loaded = Store(root: value, logger: logger);

        Assert.Equal("/chosen", loaded.Read(config => config.FederatedFolderPath));
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public void AChangeWhoseSaveFailsIsRolledBackAndRethrown()
    {
        var database = new SpyDatabase(Open());
        var store = Store(database);
        store.Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), EncryptedCredentials = Ciphertext }));
        var failure = new SqliteException("disk full", 13);
        database.WriteFailure = failure;
        var rolledBack = 0;

        var thrown = Assert.Throws<SqliteException>(() => store.Update(
            config => config.Servers[0].EncryptedCredentials = "unsaved",
            config =>
            {
                rolledBack++;
                config.Servers[0].EncryptedCredentials = Ciphertext;
            }));

        Assert.Same(failure, thrown);
        Assert.Equal(1, rolledBack);
        Assert.Equal(Ciphertext, store.Read(config => config.Servers[0].EncryptedCredentials));
    }

    [Fact]
    public void AChangeWithoutARollbackStaysInMemoryAndIsSavedWithTheNextOne()
    {
        var database = new SpyDatabase(Open());
        var store = Store(database);
        database.WriteFailure = new SqliteException("disk full", 13);

        Assert.Throws<SqliteException>(() => store.Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" })));
        Assert.Single(store.Read(config => config.Servers.ToList()));

        database.WriteFailure = null;
        store.Update(config => config.SyncIntervalSeconds = 600);

        Assert.Equal("Casa", Assert.Single(Store().Read(config => config.Servers.ToList())).Name);
    }

    [Fact]
    public void ASuspendedDatabaseGivesTheDefaultsAndRefusesChanges()
    {
        Store().Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" }));
        var database = new SpyDatabase(Open()) { Suspended = true };
        var store = Store(database);

        Assert.Empty(store.Read(config => config.Servers.ToList()));
        Assert.Throws<StoreSuspendedException>(() => store.Update(config => config.SyncIntervalSeconds = 600));
        Assert.Equal(0, database.Writes);
    }

    [Fact]
    public void ADatabaseThatHealsIsReadBeforeAnyChange()
    {
        Store().Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", EncryptedCredentials = Ciphertext }));
        var database = new SpyDatabase(Open()) { Suspended = true };
        var store = Store(database);
        Assert.Empty(store.Read(config => config.Servers.ToList()));

        database.Suspended = false;
        store.Update(config => config.SyncIntervalSeconds = 600);

        var server = Assert.Single(Store().Read(config => config.Servers.ToList()));
        Assert.Equal(("Casa", Ciphertext), (server.Name, server.EncryptedCredentials));
    }

    private PluginDatabase Open()
    {
        var database = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>(), KeyFileGuards.ForThisSystem(), SchemaMigrations.All, 1);
        _opened.Add(database);
        return database;
    }

    private PluginConfigurationStore Store(IPluginDatabase? database = null, string? root = null, CapturingLogger<PluginConfigurationStore>? logger = null)
        => new(database ?? Open(), new FixedFederatedRootSource(root), logger ?? new CapturingLogger<PluginConfigurationStore>());

    // The real database, counting writes, failing them, or suspended on demand.
    private sealed class SpyDatabase(IPluginDatabase inner) : IPluginDatabase
    {
        public int Writes { get; private set; }

        public Exception? WriteFailure { get; set; }

        public bool Suspended { get; set; }

        public StoreHealth Health => Suspended ? new StoreHealth(StoreState.Unavailable, "test") : inner.Health;

        public StoreHealth Retry() => Health;

        public Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken)
            => Suspended ? throw new StoreSuspendedException("test") : inner.ReadAsync(read, cancellationToken);

        public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
        {
            if (Suspended)
            {
                throw new StoreSuspendedException("test");
            }

            Writes++;
            return WriteFailure is { } failure ? Task.FromException<T>(failure) : inner.WriteAsync(write, cancellationToken);
        }

        public Task WriteAsync(Action<SqliteConnection, SqliteTransaction> write, CancellationToken cancellationToken)
            => WriteAsync(
                (connection, transaction) =>
                {
                    write(connection, transaction);
                    return 0;
                },
                cancellationToken);

        public Task<bool> TruncateWalAsync(CancellationToken cancellationToken) => inner.TruncateWalAsync(cancellationToken);
    }
}
