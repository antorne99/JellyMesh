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
using Jellyfin.Plugin.JellyMesh.Tests.Sync;
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

    // Defaults would show no peers, servers or folder, so a peer would pass as a local user and Forget would drop every server.
    [Fact]
    public void ASuspendedDatabaseGivesNoConfigurationAndRefusesChanges()
    {
        Store().Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" }));
        var database = new SpyDatabase(Open()) { Suspended = true };
        var store = Store(database);

        Assert.Throws<StoreSuspendedException>(() => store.Read(config => config.Servers.ToList()));
        Assert.Throws<StoreSuspendedException>(() => store.Update(config => config.SyncIntervalSeconds = 600));
        Assert.Equal(0, database.Writes);
    }

    [Fact]
    public void AFailedReadIsSuspendedAndTriedAgainAfterAPause()
    {
        Store().Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" }));
        var database = new SpyDatabase(Open()) { ReadFailure = new InvalidOperationException("database is locked") };
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var logger = new CapturingLogger<PluginConfigurationStore>();
        var store = Store(database, logger: logger, clock: clock);

        var failure = Assert.Throws<StoreSuspendedException>(() => store.Read(config => config.Servers.ToList()));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        database.ReadFailure = null;
        Assert.Throws<StoreSuspendedException>(() => store.Read(config => config.Servers.ToList()));
        Assert.Throws<StoreSuspendedException>(() => store.Update(config => config.SyncIntervalSeconds = 600));
        Assert.Equal((1, 0), (database.Reads, database.Writes));
        Assert.Single(logger.Lines);

        clock.Now += TimeSpan.FromSeconds(5);

        Assert.Equal("Casa", Assert.Single(store.Read(config => config.Servers.ToList())).Name);
    }

    [Fact]
    public void ASuspensionAfterTheLoadKeepsTheLoadedConfiguration()
    {
        Store().Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" }));
        var database = new SpyDatabase(Open());
        var store = Store(database);
        store.Read(config => config.Servers.Count);

        database.Suspended = true;

        Assert.Equal("Casa", Assert.Single(store.Read(config => config.Servers.ToList())).Name);
        Assert.Throws<StoreSuspendedException>(() => store.Update(config => config.SyncIntervalSeconds = 600));
        Assert.Equal("Casa", Assert.Single(store.Read(config => config.Servers.ToList())).Name);
    }

    [Fact]
    public void ADatabaseThatHealsIsReadBeforeAnyChange()
    {
        Store().Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", EncryptedCredentials = Ciphertext }));
        var database = new SpyDatabase(Open()) { Suspended = true };
        var store = Store(database);
        Assert.Throws<StoreSuspendedException>(() => store.Read(config => config.Servers.ToList()));

        database.Suspended = false;
        store.Update(config => config.SyncIntervalSeconds = 600);

        var server = Assert.Single(Store().Read(config => config.Servers.ToList()));
        Assert.Equal(("Casa", Ciphertext), (server.Name, server.EncryptedCredentials));
    }

    // A database opened again, as after a restore from a copy, may hold other rows: the store reads them again.
    [Fact]
    public void ADatabaseOpenedAgainIsReadAgain()
    {
        var database = new SpyDatabase(Open());
        var store = Store(database);
        store.Read(config => config.Servers.Count);
        var changes = 0;
        store.SettingsChanged += (_, _) => changes++;
        Store(database).Update(config =>
        {
            config.SyncIntervalSeconds = 1200;
            config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" });
        });
        Assert.Empty(store.Read(config => config.Servers.ToList()));

        database.Reopened++;

        Assert.Equal("Casa", Assert.Single(store.Read(config => config.Servers.ToList())).Name);
        Assert.Equal(1200, store.Read(config => config.SyncIntervalSeconds));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ADatabaseOpenedAgainThatCannotBeReadKeepsTheLoadedConfiguration()
    {
        Store().Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa" }));
        var database = new SpyDatabase(Open());
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var store = Store(database, clock: clock);
        store.Read(config => config.Servers.Count);

        database.Reopened++;
        database.ReadFailure = new InvalidOperationException("database is locked");

        Assert.Equal("Casa", Assert.Single(store.Read(config => config.Servers.ToList())).Name);
        Assert.Equal("Casa", Assert.Single(store.Read(config => config.Servers.ToList())).Name);
        Assert.Equal(2, database.Reads);

        database.ReadFailure = null;
        clock.Now += TimeSpan.FromSeconds(5);
        store.Read(config => config.Servers.Count);
        store.Read(config => config.Servers.Count);

        Assert.Equal(3, database.Reads);
    }

    // Old ciphertext stays readable in -wal until a TRUNCATE checkpoint, so a change or removal of credentials empties it.
    [Fact]
    public void ChangedOrRemovedCredentialsLeaveNoOldCiphertextOnDisk()
    {
        const string Old = "b2xkLWNpcGhlcnRleHQtc2VudGluZWwtMWYyZQ==";
        const string New = "bmV3LWNpcGhlcnRleHQtc2VudGluZWwtOWE4Yg==";
        var store = Store();
        store.Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", EncryptedCredentials = Old }));

        store.Update(config => config.Servers[0].EncryptedCredentials = New);

        Assert.DoesNotContain(Old, DatabaseText(), StringComparison.Ordinal);
        Assert.Contains(New, DatabaseText(), StringComparison.Ordinal);

        store.Update(config => config.Servers.Clear());

        Assert.DoesNotContain(New, DatabaseText(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAChangeThatDropsCredentialsEmptiesTheLog()
    {
        var database = new SpyDatabase(Open());
        var store = Store(database);

        store.Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", EncryptedCredentials = Ciphertext }));
        store.Update(config => config.Servers[0].Name = "Casa nuova");
        store.Update(config => config.SyncIntervalSeconds = 1200);
        Assert.Equal(0, database.Truncates);

        store.Update(config => config.Servers[0].EncryptedCredentials = "changed");

        Assert.Equal(1, database.Truncates);
    }

    // A reader can keep the checkpoint from finishing: the next save tries again, until one empties the log.
    [Fact]
    public void ALogThatStaysBusyIsEmptiedAtTheNextSave()
    {
        var database = new SpyDatabase(Open());
        var store = Store(database);
        store.Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", EncryptedCredentials = Ciphertext }));
        database.TruncateBusy = true;

        store.Update(config => config.Servers[0].EncryptedCredentials = "changed");
        database.TruncateBusy = false;
        store.Update(config => config.SyncIntervalSeconds = 1200);
        store.Update(config => config.SyncIntervalSeconds = 1500);

        Assert.Equal(2, database.Truncates);
    }

    // The change is saved by then: a failure to empty the log must not undo it in memory, nor reach the caller.
    [Fact]
    public void ALogThatCannotBeEmptiedKeepsTheSavedChange()
    {
        var database = new SpyDatabase(Open());
        var logger = new CapturingLogger<PluginConfigurationStore>();
        var store = Store(database, logger: logger);
        store.Update(config => config.Servers.Add(new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", EncryptedCredentials = Ciphertext }));
        database.TruncateFailure = new SqliteException("disk I/O error", 10);
        var rolledBack = 0;

        store.Update(config => config.Servers[0].EncryptedCredentials = "changed", _ => rolledBack++);

        Assert.Equal(0, rolledBack);
        Assert.Equal("changed", store.Read(config => config.Servers[0].EncryptedCredentials));
        Assert.Equal("changed", Store().Read(config => config.Servers[0].EncryptedCredentials));
        Assert.Single(logger.Lines);
    }

    private PluginDatabase Open()
    {
        var database = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>(), KeyFileGuards.ForThisSystem(), SchemaMigrations.All, 1);
        _opened.Add(database);
        return database;
    }

    // The database file and its log as bytes on disk, which a copy of the data folder would hold.
    private string DatabaseText()
    {
        var text = new System.Text.StringBuilder();
        foreach (var path in new[] { PluginDatabase.FileName, PluginDatabase.FileName + "-wal" }.Select(name => Path.Combine(Folder, name)).Where(File.Exists))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private PluginConfigurationStore Store(IPluginDatabase? database = null, string? root = null, CapturingLogger<PluginConfigurationStore>? logger = null, TimeProvider? clock = null)
        => new(database ?? Open(), new FixedFederatedRootSource(root), logger ?? new CapturingLogger<PluginConfigurationStore>(), clock ?? TimeProvider.System);

    // The real database, counting writes, failing them, or suspended on demand.
    private sealed class SpyDatabase(IPluginDatabase inner) : IPluginDatabase
    {
        public int Writes { get; private set; }

        public Exception? WriteFailure { get; set; }

        public bool Suspended { get; set; }

        public Exception? ReadFailure { get; set; }

        public int Reads { get; private set; }

        public StoreHealth Health => Suspended ? new StoreHealth(StoreState.Unavailable, "test") : inner.Health;

        // Added to the database's own, as a reopen after a retry or a restore from a copy does.
        public long Reopened { get; set; }

        public long Generation => inner.Generation + Reopened;

        public StoreRecovery? Recovery => inner.Recovery;

        public void DismissRecovery() => inner.DismissRecovery();

        public StoreHealth Retry() => Health;

        public Task<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, T> read, CancellationToken cancellationToken)
        {
            if (Suspended)
            {
                throw new StoreSuspendedException("test");
            }

            Reads++;
            return ReadFailure is { } failure ? Task.FromException<T>(failure) : inner.ReadAsync(read, cancellationToken);
        }

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

        public int Truncates { get; private set; }

        public bool TruncateBusy { get; set; }

        public Exception? TruncateFailure { get; set; }

        public async Task<bool> TruncateWalAsync(CancellationToken cancellationToken)
        {
            Truncates++;
            if (TruncateFailure is { } failure)
            {
                throw failure;
            }

            var emptied = await inner.TruncateWalAsync(cancellationToken);
            return emptied && !TruncateBusy;
        }

        public Task<StoreBackup> BackUpAsync(CancellationToken cancellationToken) => inner.BackUpAsync(cancellationToken);

        public Task DeleteExpiredDamagedAsync(CancellationToken cancellationToken) => inner.DeleteExpiredDamagedAsync(cancellationToken);
    }
}
