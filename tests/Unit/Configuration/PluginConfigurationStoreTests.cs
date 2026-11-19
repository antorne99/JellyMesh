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
            config.NewServerLibraryMode = LibraryMode.ByRemoteLibrary;

            var server = new RemoteServerConfig
            {
                Id = serverId,
                JellyfinServerId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b",
                Name = "Casa",
                Url = TestValues.Url,
                EncryptedCredentials = Ciphertext,
                Mode = RemoteMode.JellyMesh,
                ProtocolVersion = 2,
                LibraryMode = LibraryMode.ByRemoteLibrary,
                FollowSkipped = true,
                RenameUnfinished = true
            };
            server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = "library-2", Name = "Cartoni", CollectionType = "tvshows" });
            server.RemoteLibraries.Add(new RemoteLibraryConfig { Id = "library-1", Name = "Film 4K", CollectionType = "movies" });
            server.CreatedLibraryKeys.Add("library-2");
            server.CreatedLibraryKeys.Add("Movies");
            var pending = new PendingLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film 4K", RemoteLibraryId = "library-1" };
            pending.Paths.Add("/federata/b/library-1/Film");
            pending.Paths.Add("/federata/b/library-1/Film\nnuova");
            server.PendingLibraries.Add(pending);
            server.ImportedLibraryIds.Add("library-2");
            server.ImportedLibraryIds.Add("library-1");
            server.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Casa - Serie", ItemId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0", RemoteLibraryId = "library-2" });
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

        Assert.Equal(LibraryMode.ByRemoteLibrary, loaded.NewServerLibraryMode);
        Assert.Equal(["Casa", "Amici"], loaded.Servers.Select(server => server.Name));
        var casa = loaded.Servers[0];
        Assert.Equal(
            (serverId, "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b", TestValues.Url, Ciphertext, RemoteMode.JellyMesh, 2, true, true, true),
            (casa.Id, casa.JellyfinServerId, casa.Url, casa.EncryptedCredentials, casa.Mode, casa.ProtocolVersion, casa.AutomaticLibraries, casa.FollowSkipped, casa.RenameUnfinished));
        Assert.Equal(["library-2", "library-1"], casa.ImportedLibraryIds);
        Assert.Equal((LibraryMode.ByRemoteLibrary, LibraryMode.None), (casa.LibraryMode, loaded.Servers[1].LibraryMode));
        Assert.Equal(
            [("library-2", "Cartoni", "tvshows"), ("library-1", "Film 4K", "movies")],
            casa.RemoteLibraries.Select(library => (library.Id, library.Name, library.CollectionType)));
        Assert.Equal(["library-2", "Movies"], casa.CreatedLibraryKeys);
        var loadedPending = Assert.Single(casa.PendingLibraries);
        Assert.Equal((ServerLibraryKind.Movies, "Casa - Film 4K", "library-1"), (loadedPending.Kind, loadedPending.Name, loadedPending.RemoteLibraryId));
        Assert.Equal(["/federata/b/library-1/Film", "/federata/b/library-1/Film\nnuova"], loadedPending.Paths);
        Assert.Equal(
            [(ServerLibraryKind.Series, "Casa - Serie", "0f1e2d3c4b5a69788796a5b4c3d2e1f0", "library-2"), (ServerLibraryKind.Movies, "Casa - Film", string.Empty, string.Empty)],
            casa.AutomaticLibraryRegistry.Select(library => (library.Kind, library.Name, library.ItemId, library.RemoteLibraryId)));
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
    public void TheLibrariesToPlaceOfAServerSurviveARestart()
    {
        var serverId = Guid.NewGuid();
        Store().Update(config =>
        {
            var server = new RemoteServerConfig { Id = serverId, Name = "Casa", Url = TestValues.Url };
            server.LibrariesToPlace.Add("b0b1b2b3b4b5b6b7b8b9babbbcbdbebf");
            server.LibrariesToPlace.Add("a0a1a2a3a4a5a6a7a8a9aaabacadaeaf");
            config.Servers.Add(server);
        });

        var loaded = Store().Read(config => config);

        Assert.Equal(["b0b1b2b3b4b5b6b7b8b9babbbcbdbebf", "a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"], loaded.Servers[0].LibrariesToPlace);
    }

    [Fact]
    public void TheFoldersPlacedOfAServerSurviveARestart()
    {
        var serverId = Guid.NewGuid();
        Store().Update(config =>
        {
            var server = new RemoteServerConfig { Id = serverId, Name = "Casa", Url = TestValues.Url };
            server.PlacedFolders.Add(new PlacedFolderConfig { Library = "0f1e2d3c4b5a69788796a5b4c3d2e1f0", Folder = "a/library-1/Film" });
            server.PlacedFolders.Add(new PlacedFolderConfig { Library = "Film di casa", Folder = "a/Serie" });
            config.Servers.Add(server);
        });

        var loaded = Store().Read(config => config);

        Assert.Equal(
            [("0f1e2d3c4b5a69788796a5b4c3d2e1f0", "a/library-1/Film"), ("Film di casa", "a/Serie")],
            loaded.Servers[0].PlacedFolders.Select(placed => (placed.Library, placed.Folder)));
    }

    [Fact]
    public void AnAdoptedLibraryStaysAdoptedAfterARestartForAServerAndARemovedOne()
    {
        var serverId = Guid.NewGuid();
        var removedId = Guid.NewGuid();
        Store().Update(config =>
        {
            var server = new RemoteServerConfig { Id = serverId, Name = "Casa", Url = TestValues.Url };
            server.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film" });
            server.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Serie di casa", Adopted = true });
            config.Servers.Add(server);
            config.HoldAutomaticLibraries(
                removedId,
                [
                    new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Film di via", Adopted = true },
                    new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Via - Film" }
                ],
                [],
                [],
                false);
        });

        var loaded = Store().Read(config => config);

        Assert.Equal([("Casa - Film", false), ("Serie di casa", true)], loaded.Servers[0].AutomaticLibraryRegistry.Select(library => (library.Name, library.Adopted)));
        Assert.Equal([("Film di via", true), ("Via - Film", false)], loaded.RemovedServerLibraries[0].Libraries.Select(library => (library.Name, library.Adopted)));
    }

[Fact]
    public void TheSharedLibrariesSurviveARestart()
    {
        var serverId = Guid.NewGuid();
        Store().Update(config =>
        {
            config.Servers.Add(new RemoteServerConfig { Id = serverId, Name = "Casa", Url = TestValues.Url });
            var shared = config.SharedLibraries;
            shared.Enabled = true;
            shared.FollowSkipped = true;
            var film = new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Tutti i server - Film", ItemId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0", Unblocked = true };
            film.Paths.Add("/federata/a/Film");
            film.Paths.Add("/federata/b/Film");
            shared.Libraries.Add(film);
            var series = new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Serie di tutti", Adopted = true };
            series.Paths.Add("/federata/a/Serie");
            shared.Libraries.Add(series);
            shared.CreatedLibraryKeys.Add("Movies");
            var pending = new PendingLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Tutti i server - Serie" };
            pending.Paths.Add("/federata/a/library-1/Serie");
            shared.PendingLibraries.Add(pending);
            shared.PreexistingLibraryIds.Add("9a8b7c6d5e4f30211203f4e5d6c7b8a9");
            shared.FollowedLibraryIds.Add("5d6c7b8a99a8b7c6d5e4f30211203f4e");
            shared.ReleasedLibraries.Add("Tutti i server - Vecchia");
        });

        var loaded = Store().Read(config => config);

        var shared = loaded.SharedLibraries;
        Assert.Equal((true, true), (shared.Enabled, shared.FollowSkipped));
        Assert.Equal(
            [(ServerLibraryKind.Movies, "Tutti i server - Film", "0f1e2d3c4b5a69788796a5b4c3d2e1f0", false), (ServerLibraryKind.Series, "Serie di tutti", string.Empty, true)],
            shared.Libraries.Select(library => (library.Kind, library.Name, library.ItemId, library.Adopted)));
        Assert.Equal([["/federata/a/Film", "/federata/b/Film"], ["/federata/a/Serie"]], shared.Libraries.Select(library => library.Paths.ToList()));
        Assert.Equal([true, false], shared.Libraries.Select(library => library.Unblocked));
        Assert.Equal(["Movies", "Series"], shared.CreatedLibraryKeys);
        var loadedPending = Assert.Single(shared.PendingLibraries);
        Assert.Equal((ServerLibraryKind.Series, "Tutti i server - Serie"), (loadedPending.Kind, loadedPending.Name));
        Assert.Equal(["/federata/a/library-1/Serie"], loadedPending.Paths);
        Assert.Equal(["9a8b7c6d5e4f30211203f4e5d6c7b8a9"], shared.PreexistingLibraryIds);
        Assert.Equal(["5d6c7b8a99a8b7c6d5e4f30211203f4e"], shared.FollowedLibraryIds);
        Assert.Equal(["Tutti i server - Vecchia"], shared.ReleasedLibraries);
        var server = Assert.Single(loaded.Servers);
        Assert.Empty(server.AutomaticLibraryRegistry);
        Assert.Empty(server.PendingLibraries);
    }

    [Fact]
    public void TheSharedLibrariesAreOffByDefault()
    {
        var loaded = Store().Read(config => config);

        Assert.False(loaded.SharedLibraries.Enabled);
        Assert.Empty(loaded.SharedLibraries.Libraries);
    }

    [Fact]
    public void AbsentAndVanishedLibrariesSurviveARestart()
    {
        var serverId = Guid.NewGuid();
        Store().Update(config =>
        {
            var server = new RemoteServerConfig { Id = serverId, Name = "Casa", Url = TestValues.Url };
            server.AbsentLibraryIds.Add(TestLibraries.FilmsB);
            server.VanishedLibraries.Add(new VanishedLibraryConfig { Id = TestLibraries.FilmsC, Name = "Film vecchi" });
            server.VanishedLibraries.Add(new VanishedLibraryConfig { Id = TestLibraries.ShowsC, Name = "Serie vecchie" });
            config.Servers.Add(server);
        });

        var loaded = Store().Read(config => config.Servers.Single());

        Assert.Equal([TestLibraries.FilmsB], loaded.AbsentLibraryIds);
        Assert.Equal([(TestLibraries.FilmsC, "Film vecchi"), (TestLibraries.ShowsC, "Serie vecchie")], loaded.VanishedLibraries.Select(library => (library.Id, library.Name)));
    }

    [Fact]
    public void TheUsersWhoLostTitlesInAModeChangeSurviveARestart()
    {
        Store().Update(config =>
        {
            var server = new RemoteServerConfig { Id = Guid.NewGuid(), Name = "Casa", Url = TestValues.Url };
            server.LostAccess.Add(new LostAccessConfig { Library = "Casa - Film", User = "bambini" });
            server.LostAccess.Add(new LostAccessConfig { Library = "Casa - Film", User = "noadulti" });
            config.Servers.Add(server);
        });

        var loaded = Store().Read(config => config.Servers.Single());

        Assert.Equal([("Casa - Film", "bambini"), ("Casa - Film", "noadulti")], loaded.LostAccess.Select(lost => (lost.Library, lost.User)));
    }

    [Fact]
    public async Task AServerKeptBeforeTheLibraryModesHasThemPerTypeWhereItsLibrariesWereOnAndNoneElsewhere()
    {
        var on = Guid.NewGuid();
        var off = Guid.NewGuid();
        var before = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>(), KeyFileGuards.ForThisSystem(), SchemaMigrations.All.Take(4).ToList(), 1);
        await before.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    INSERT INTO servers VALUES ('{on:D}', 0, '', 'Casa', 'http://casa.invalid', '', 'Unknown', 0, 1, 0, 0);
                    INSERT INTO servers VALUES ('{off:D}', 1, '', 'Amici', 'http://amici.invalid', '', 'Unknown', 0, 0, 0, 0);
                    INSERT INTO server_automatic_libraries VALUES ('{on:D}', 0, 'Movies', 'Casa - Film', 'item-1');
                    """;
                command.ExecuteNonQuery();
            },
            CancellationToken.None);
        before.Dispose();
        SqliteConnection.ClearAllPools();

        var loaded = Store().Read(config => config);

        Assert.Equal([LibraryMode.ByType, LibraryMode.None], loaded.Servers.Select(server => server.LibraryMode));
        Assert.Equal([true, false], loaded.Servers.Select(server => server.AutomaticLibraries));
        Assert.Equal(("Casa - Film", string.Empty), Assert.Single(loaded.Servers[0].AutomaticLibraryRegistry) is var library ? (library.Name, library.RemoteLibraryId) : default);
        Assert.Equal(LibraryMode.ByType, loaded.NewServerLibraryMode);
    }

    [Fact]
    public async Task AnAutomaticLibraryKeptBeforeTheLibraryModesCountsAsMadeSoItIsNeverMadeAgain()
    {
        var on = Guid.NewGuid();
        var before = new PluginDatabase(Folder, new CapturingLogger<PluginDatabase>(), KeyFileGuards.ForThisSystem(), SchemaMigrations.All.Take(4).ToList(), 1);
        await before.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    INSERT INTO servers VALUES ('{on:D}', 0, '', 'Casa', 'http://casa.invalid', '', 'Unknown', 0, 1, 0, 0);
                    INSERT INTO server_automatic_libraries VALUES ('{on:D}', 0, 'Movies', 'Casa - Film', 'item-1');
                    INSERT INTO server_automatic_libraries VALUES ('{on:D}', 1, 'Series', 'Casa - Serie', 'item-2');
                    """;
                command.ExecuteNonQuery();
            },
            CancellationToken.None);
        before.Dispose();
        SqliteConnection.ClearAllPools();
        var store = Store();

        Assert.Equal(["Movies", "Series"], store.Read(config => config.Servers[0].CreatedLibraryKeys.ToList()));

        // Once a follow drops the record of a deleted library, the key stays.
        store.Update(config => config.Servers[0].AutomaticLibraryRegistry.RemoveAt(0));
        SqliteConnection.ClearAllPools();
        Assert.Equal(["Movies", "Series"], Store().Read(config => config.Servers[0].CreatedLibraryKeys.ToList()));
    }

    [Fact]
    public void TurningLibrariesOnKeepsTheModeChosenAndChoosesPerTypeOtherwise()
    {
        var server = new RemoteServerConfig { LibraryMode = LibraryMode.ByRemoteLibrary };
        server.AutomaticLibraries = true;
        var kept = server.LibraryMode;
        server.AutomaticLibraries = false;
        var off = server.LibraryMode;
        server.AutomaticLibraries = true;

        Assert.Equal((LibraryMode.ByRemoteLibrary, LibraryMode.None, LibraryMode.ByType), (kept, off, server.LibraryMode));
    }

    [Fact]
    public void AnEmptyDatabaseGivesTheDefaults()
    {
        var loaded = Store().Read(config => config);

        Assert.Equal(LibraryMode.ByType, loaded.NewServerLibraryMode);
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

        public Task<bool> DeleteExpiredMigratedAsync(CancellationToken cancellationToken) => inner.DeleteExpiredMigratedAsync(cancellationToken);
    }
}
