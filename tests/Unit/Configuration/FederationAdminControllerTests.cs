using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

public class FederationAdminControllerTests
{
    private static readonly AddServerRequest _request = new()
    {
        Name = "Friend",
        Url = TestValues.Url,
        Username = TestValues.Username,
        Password = TestValues.Password
    };

    private static FederationAdminController ControllerFor(RegistryFixture fixture, IFederatedFolderLinkChecker? checker = null, string? federatedRoot = null, IServerLibraryManager? libraries = null, ILayoutMigration? migration = null, ILibraryChangeTracker? changes = null, IServerRenamer? renamer = null)
        => new(
            fixture.Registry,
            fixture.Vault,
            fixture.Store,
            checker ?? new RecordingChecker(FederatedFolderLinkState.NotChecked),
            new CoordinatorFixture().Coordinator,
            new FixedFederatedRootSource(federatedRoot),
            Mock.Of<ICatalogSyncService>(),
            new InMemoryMergeStore(),
            libraries ?? Mock.Of<IServerLibraryManager>(),
            new CapturingLogger<FederationAdminController>(),
            migration,
            changes,
            renamer);

    [Fact]
    public async Task Removing_a_server_closes_its_sessions_while_it_can_still_be_reached()
    {
        var playback = new CoordinatorFixture();
        var source = PlaybackValues.Source();
        playback.Registry.AddJellyMesh(source.ServerId);
        await playback.OpenAsync(PlaybackValues.Stub(source));
        var releasedBeforeRemoval = -1;
        var markedBeforeRemoval = false;
        var marked = false;
        var releasedWhenMarked = -1;
        playback.Registry.OnRemove = () =>
        {
            releasedBeforeRemoval = playback.Client.Released.Count;
            markedBeforeRemoval = marked;
        };
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(service => service.RemoveServerAsync(source.ServerId, It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Func<Task<bool>>, CancellationToken>((_, remove, _) =>
            {
                marked = true;
                releasedWhenMarked = playback.Client.Released.Count;
                return remove();
            });
        var controller = new FederationAdminController(
            playback.Registry,
            new RegistryFixture().Vault,
            playback.Store,
            new RecordingChecker(FederatedFolderLinkState.NotChecked),
            playback.Coordinator,
            new FixedFederatedRootSource(null),
            sync.Object,
            new InMemoryMergeStore(),
            Mock.Of<IServerLibraryManager>(),
            new CapturingLogger<FederationAdminController>());

        var result = await controller.RemoveServer(source.ServerId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, releasedBeforeRemoval);
        Assert.True(markedBeforeRemoval);
        Assert.Equal(0, releasedWhenMarked);
        Assert.Empty(playback.Sessions.All);
        sync.Verify(service => service.StartRemovalCleanup(), Times.Once);
    }

    [Fact]
    public async Task Removing_an_unknown_server_deletes_nothing()
    {
        var sync = new Mock<ICatalogSyncService>();
        using var fixture = new RegistryFixture();
        var controller = new FederationAdminController(
            fixture.Registry,
            fixture.Vault,
            fixture.Store,
            new RecordingChecker(FederatedFolderLinkState.NotChecked),
            new CoordinatorFixture().Coordinator,
            new FixedFederatedRootSource(null),
            sync.Object,
            new InMemoryMergeStore(),
            Mock.Of<IServerLibraryManager>(),
            new CapturingLogger<FederationAdminController>());

        var result = await controller.RemoveServer(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        sync.Verify(service => service.StartRemovalCleanup(), Times.Never);
        sync.Verify(service => service.RemoveServerAsync(It.IsAny<Guid>(), It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task A_removal_that_cannot_be_saved_keeps_the_server_and_says_to_try_again(Type saveFailure)
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(service => service.RemoveServerAsync(server.Id, It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Func<Task<bool>>, CancellationToken>((_, remove, _) => remove());
        var playback = new CoordinatorFixture();
        var controller = new FederationAdminController(
            fixture.Registry,
            fixture.Vault,
            fixture.Store,
            new RecordingChecker(FederatedFolderLinkState.NotChecked),
            playback.Coordinator,
            new FixedFederatedRootSource(null),
            sync.Object,
            new InMemoryMergeStore(),
            Mock.Of<IServerLibraryManager>(),
            new CapturingLogger<FederationAdminController>());
        fixture.Store.SaveFailure = (Exception)Activator.CreateInstance(saveFailure)!;

        var result = Assert.IsType<ObjectResult>(await controller.RemoveServer(server.Id, CancellationToken.None));

        Assert.Equal(500, result.StatusCode);
        var failure = Assert.IsType<FailureResponse>(result.Value);
        Assert.Equal(RemoteFailure.RemovalFailedPlaybackStopped, failure.Failure);
        Assert.Contains("playback from it was stopped", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Try again", failure.Message, StringComparison.Ordinal);
        Assert.Equal(server.Id, Assert.Single(fixture.Registry.GetServers()).Id);
        sync.Verify(service => service.StartRemovalCleanup(), Times.Never);

        // The server stays, so its login stays and it plays again (413).
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
        var stream = await playback.Coordinator.OpenAsync(server.Id, new RemoteStreamRequest(System.Net.Http.HttpMethod.Get, "/Videos/a/stream", null), CancellationToken.None);
        Assert.True(stream.IsSuccess, stream.Failure.ToString());
        stream.Value!.Dispose();
    }

    // Decision 413: what plays from the server ends first, then its login on the remote.
    [Fact]
    public async Task Removing_a_server_ends_its_streams_then_its_login_on_the_remote()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var playback = new CoordinatorFixture();
        var loggedOutAtClose = true;
        playback.Transcodes.Setup(manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()))
            .Callback(() => loggedOutAtClose = fixture.Handler.Requests.Any(request => request.Uri.AbsolutePath == "/Sessions/Logout"))
            .Returns(Task.CompletedTask);
        playback.Coordinator.RecordLocalPlaySession(Guid.NewGuid(), Guid.NewGuid(), "local", ["device"]);
        playback.Coordinator.RecordLocalTranscode("local", new StubSource(server.Id, "item", "item-ms"));
        var controller = new FederationAdminController(
            fixture.Registry,
            fixture.Vault,
            fixture.Store,
            new RecordingChecker(FederatedFolderLinkState.NotChecked),
            playback.Coordinator,
            new FixedFederatedRootSource(null),
            RemovingSync(server.Id).Object,
            new InMemoryMergeStore(),
            Mock.Of<IServerLibraryManager>(),
            new CapturingLogger<FederationAdminController>());

        Assert.IsType<NoContentResult>(await controller.RemoveServer(server.Id, CancellationToken.None));

        Assert.False(loggedOutAtClose);
        playback.Transcodes.Verify(manager => manager.KillTranscodingJobs(string.Empty, "local", It.IsAny<Func<string, bool>>()), Times.Once);
        Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
    }

    [Fact]
    public async Task A_removal_whose_mark_cannot_be_saved_stops_nothing_and_says_to_try_again()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(service => service.RemoveServerAsync(server.Id, It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));
        var controller = new FederationAdminController(
            fixture.Registry,
            fixture.Vault,
            fixture.Store,
            new RecordingChecker(FederatedFolderLinkState.NotChecked),
            new CoordinatorFixture().Coordinator,
            new FixedFederatedRootSource(null),
            sync.Object,
            new InMemoryMergeStore(),
            Mock.Of<IServerLibraryManager>(),
            new CapturingLogger<FederationAdminController>());

        var result = Assert.IsType<ObjectResult>(await controller.RemoveServer(server.Id, CancellationToken.None));

        Assert.Equal(500, result.StatusCode);
        var failure = Assert.IsType<FailureResponse>(result.Value);
        Assert.Equal(RemoteFailure.RemovalFailed, failure.Failure);
        Assert.DoesNotContain("playback", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Try again", failure.Message, StringComparison.Ordinal);
        Assert.Equal(server.Id, Assert.Single(fixture.Registry.GetServers()).Id);
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(0, 0)]
    public async Task Forgetting_missing_servers_says_how_many_and_cleans_up_only_when_some_were_forgotten(int forgotten, int cleanups)
    {
        using var fixture = new RegistryFixture();
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(service => service.ForgetMissingServersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(forgotten);
        var controller = CreateController(fixture, sync.Object);

        var result = await controller.ForgetMissingServers(CancellationToken.None);

        Assert.Equal(forgotten, Assert.IsType<ForgetResponse>(result.Value).Servers);
        sync.Verify(service => service.StartRemovalCleanup(), Times.Exactly(cleanups));
    }

    [Fact]
    public async Task Forgetting_missing_servers_while_the_settings_cannot_be_read_is_unavailable()
    {
        using var fixture = new RegistryFixture();
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(service => service.ForgetMissingServersAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new StoreSuspendedException("never read"));
        var controller = CreateController(fixture, sync.Object);

        var result = Assert.IsType<ObjectResult>((await controller.ForgetMissingServers(CancellationToken.None)).Result);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal(StoreUnavailableFilter.Message, Assert.IsType<FailureResponse>(result.Value).Message);
        sync.Verify(service => service.StartRemovalCleanup(), Times.Never);
    }

    [Fact]
    public async Task Forgetting_missing_servers_that_cannot_be_saved_says_to_try_again()
    {
        using var fixture = new RegistryFixture();
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(service => service.ForgetMissingServersAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));
        var controller = CreateController(fixture, sync.Object);

        var result = Assert.IsType<ObjectResult>((await controller.ForgetMissingServers(CancellationToken.None)).Result);

        Assert.Equal(500, result.StatusCode);
        var failure = Assert.IsType<FailureResponse>(result.Value);
        Assert.Equal(RemoteFailure.ForgetFailed, failure.Failure);
        Assert.Contains("Try again", failure.Message, StringComparison.Ordinal);
        sync.Verify(service => service.StartRemovalCleanup(), Times.Never);
    }

    [Fact]
    public async Task Removing_a_server_takes_its_copies_out_of_the_manual_merges()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var removed = server.Id.ToString("N");
        const string Other = "0123456789abcdef0123456789abcdef";
        var withLocal = Merge(Copy("local", "l1", isLocal: true), Copy(removed, "a"));
        var remotesOnly = Merge(Copy(Other, "b"), Copy(removed, "c"));
        var threeCopies = Merge(Copy("local", "l2", isLocal: true), Copy(removed, "d"), Copy(Other, "e"));
        var untouched = Merge(Copy(Other, "f"), Copy("local", "l3", isLocal: true));
        var merges = new InMemoryMergeStore();
        merges.Set(withLocal, remotesOnly, threeCopies, untouched);
        var sync = RemovingSync(server.Id);
        var mergesAtCleanup = -1;
        sync.Setup(service => service.StartRemovalCleanup()).Callback(() => mergesAtCleanup = merges.Merges.Count);
        var controller = CreateController(fixture, sync.Object, merges);

        var result = await controller.RemoveServer(server.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(2, merges.Merges.Count);
        Assert.Equal(2, mergesAtCleanup);
        var kept = Assert.Single(merges.Merges, merge => merge.Id == threeCopies.Id);
        Assert.Equal(threeCopies.CreatedUtc, kept.CreatedUtc);
        Assert.Equal(["l2", "e"], kept.Copies.Select(copy => copy.ItemId));
        Assert.Same(untouched, Assert.Single(merges.Merges, merge => merge.Id == untouched.Id));
        sync.Verify(service => service.StartRemovalCleanup(), Times.Once);
    }

    [Fact]
    public async Task Removing_a_server_deletes_its_automatic_libraries_with_no_confirmation()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var record = new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film", ItemId = "a1" };
        fixture.Store.Update(config =>
        {
            var stored = config.Servers.Single();
            stored.AutomaticLibraries = true;
            stored.AutomaticLibraryRegistry.Add(record);
        });
        var libraries = new Mock<IServerLibraryManager>();
        var heldAtStart = 0;
        libraries.Setup(manager => manager.DeleteRemovedServerLibraries())
            .Callback(() => heldAtStart = fixture.Store.Configuration.RemovedServerLibraries.Sum(entry => entry.Libraries.Count))
            .Returns(Task.CompletedTask);
        var controller = CreateController(fixture, RemovingSync(server.Id).Object, libraries: libraries.Object);

        var result = await controller.RemoveServer(server.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, heldAtStart);
        libraries.Verify(manager => manager.DeleteRemovedServerLibraries(), Times.Once);
        libraries.Verify(manager => manager.Disable(It.IsAny<Guid>(), It.IsAny<bool>()), Times.Never);
    }

    // Jellyfin hands a primary version over only when its item alone is deleted, not with its library.
    [Fact]
    public async Task The_automatic_libraries_of_a_removed_server_go_after_its_stubs()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var cleanup = new TaskCompletionSource();
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = RemovingSync(server.Id);
        sync.Setup(service => service.StartRemovalCleanup()).Returns(cleanup.Task);
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.DeleteRemovedServerLibraries())
            .Callback(() => deleted.SetResult())
            .Returns(Task.CompletedTask);
        var controller = CreateController(fixture, sync.Object, libraries: libraries.Object);

        await controller.RemoveServer(server.Id, CancellationToken.None);

        libraries.Verify(manager => manager.DeleteRemovedServerLibraries(), Times.Never);
        cleanup.SetResult();
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_removal_that_fails_deletes_no_automatic_library()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var libraries = new Mock<IServerLibraryManager>();
        var controller = CreateController(fixture, RemovingSync(server.Id).Object, libraries: libraries.Object);
        fixture.Store.SaveFailure = new IOException("disk full");

        await controller.RemoveServer(server.Id, CancellationToken.None);

        libraries.Verify(manager => manager.DeleteRemovedServerLibraries(), Times.Never);
    }

    // D33: the kept watch state gets the merges of the removed server before its copies leave them.
    [Fact]
    public async Task Removing_a_server_keeps_its_merges_for_the_watch_state_before_taking_its_copies_out()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var removed = Merge(Copy("local", "l1", isLocal: true), Copy(server.Id.ToString("N"), "a"));
        var untouched = Merge(Copy("local", "l2", isLocal: true), Copy("0123456789abcdef0123456789abcdef", "b"));
        var merges = new InMemoryMergeStore();
        merges.Set(removed, untouched);
        var mergesAtKeep = -1;
        var keeper = new Mock<IWatchStateKeeper>();
        keeper.Setup(k => k.KeepMergesAsync(server.Id, It.IsAny<IReadOnlyList<ManualMerge>>(), It.IsAny<CancellationToken>()))
            .Callback(() => mergesAtKeep = merges.Merges.Count)
            .ReturnsAsync(true);
        var controller = CreateController(fixture, RemovingSync(server.Id).Object, merges, keeper: keeper.Object);

        await controller.RemoveServer(server.Id, CancellationToken.None);

        keeper.Verify(k => k.KeepMergesAsync(server.Id, It.Is<IReadOnlyList<ManualMerge>>(kept => kept.Single() == removed), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, mergesAtKeep);
        Assert.Same(untouched, Assert.Single(merges.Merges));
    }

    // A server added back with its id and removed again replaces the merges kept the first time, even with none now.
    [Fact]
    public async Task Removing_a_server_without_merges_clears_those_kept_for_it_before()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var keeper = new Mock<IWatchStateKeeper>();
        keeper.Setup(k => k.KeepMergesAsync(server.Id, It.IsAny<IReadOnlyList<ManualMerge>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var merges = new InMemoryMergeStore();
        var controller = CreateController(fixture, RemovingSync(server.Id).Object, merges, keeper: keeper.Object);

        await controller.RemoveServer(server.Id, CancellationToken.None);

        keeper.Verify(k => k.KeepMergesAsync(server.Id, It.Is<IReadOnlyList<ManualMerge>>(kept => kept.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, merges.Updates);
    }

    // Merges that cannot be kept for the watch state keep the copies of the removed server, shown as not found.
    [Fact]
    public async Task Merges_that_cannot_be_kept_for_the_watch_state_keep_the_copies_of_the_removed_server()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var merge = Merge(Copy("local", "l1", isLocal: true), Copy(server.Id.ToString("N"), "a"));
        var merges = new InMemoryMergeStore();
        merges.Set(merge);
        var keeper = new Mock<IWatchStateKeeper>();
        keeper.Setup(k => k.KeepMergesAsync(server.Id, It.IsAny<IReadOnlyList<ManualMerge>>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sync = RemovingSync(server.Id);
        var controller = CreateController(fixture, sync.Object, merges, keeper: keeper.Object);

        var result = await controller.RemoveServer(server.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Same(merge, Assert.Single(merges.Merges));
        sync.Verify(service => service.StartRemovalCleanup(), Times.Once);
    }

    [Fact]
    public async Task A_removal_that_fails_keeps_the_manual_merges()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var merges = new InMemoryMergeStore();
        merges.Set(Merge(Copy("local", "l1", isLocal: true), Copy(server.Id.ToString("N"), "a")));
        var sync = RemovingSync(server.Id);
        var controller = CreateController(fixture, sync.Object, merges);
        fixture.Store.SaveFailure = new IOException("disk full");

        var result = Assert.IsType<ObjectResult>(await controller.RemoveServer(server.Id, CancellationToken.None));

        Assert.Equal(500, result.StatusCode);
        Assert.Single(merges.Merges);
        Assert.Equal(0, merges.Updates);
    }

    [Fact]
    public async Task A_removal_whose_server_has_no_merge_writes_no_merges()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var merges = new InMemoryMergeStore { IsDamaged = true };
        var controller = CreateController(fixture, RemovingSync(server.Id).Object, merges);

        var result = await controller.RemoveServer(server.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(0, merges.Updates);
        Assert.True(merges.IsDamaged);
    }

    [Fact]
    public async Task Merges_that_cannot_be_saved_after_a_removal_leave_the_server_removed_and_are_logged()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var merges = new InMemoryMergeStore { UpdateFailure = new IOException("disk full at " + TestValues.Host) };
        merges.Set(Merge(Copy("local", "l1", isLocal: true), Copy(server.Id.ToString("N"), "a")));
        var logger = new CapturingLogger<FederationAdminController>();
        var sync = RemovingSync(server.Id);
        var controller = CreateController(fixture, sync.Object, merges, logger);

        var result = await controller.RemoveServer(server.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(fixture.Registry.GetServers());
        sync.Verify(service => service.StartRemovalCleanup(), Times.Once);
        var line = Assert.Single(logger.Lines);
        Assert.Contains("IOException", line, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, line, StringComparison.Ordinal);
    }

    private static Mock<ICatalogSyncService> RemovingSync(Guid serverId)
    {
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(service => service.RemoveServerAsync(serverId, It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Func<Task<bool>>, CancellationToken>((_, remove, _) => remove());
        return sync;
    }

    private static MergedCopy Copy(string serverId, string itemId, bool isLocal = false)
        => new() { ServerId = serverId, ItemId = itemId, IsLocal = isLocal };

    private static ManualMerge Merge(params MergedCopy[] copies)
        => new() { Id = Guid.NewGuid(), CreatedUtc = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), Copies = copies };

    private static FederationAdminController CreateController(
        RegistryFixture fixture,
        ICatalogSyncService sync,
        IManualMergeStore? merges = null,
        ILogger<FederationAdminController>? logger = null,
        IServerLibraryManager? libraries = null,
        IWatchStateKeeper? keeper = null) => new(
        fixture.Registry,
        fixture.Vault,
        fixture.Store,
        new RecordingChecker(FederatedFolderLinkState.NotChecked),
        new CoordinatorFixture().Coordinator,
        new FixedFederatedRootSource(null),
        sync,
        merges ?? new InMemoryMergeStore(),
        libraries ?? Mock.Of<IServerLibraryManager>(),
        logger ?? new CapturingLogger<FederationAdminController>(),
        keeper: keeper);

    private static string ToJson(object? value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), JsonDefaults.Options);

    private static string ToXml(PluginConfiguration config)
    {
        using var writer = new System.IO.StringWriter();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, config);
        return writer.ToString();
    }

    private static void AssertNoSecret(string text)
    {
        Assert.DoesNotContain(TestValues.Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Key, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Token, text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEndpointRequiresAnAdministrator()
    {
        var attribute = typeof(FederationAdminController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(Policies.RequiresElevation, attribute!.Policy);
        Assert.DoesNotContain(
            typeof(FederationAdminController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
    }

    [Fact]
    public void TheAdminRouteDoesNotAnswerTheCapabilityPath()
    {
        var route = typeof(FederationAdminController).GetCustomAttribute<RouteAttribute>()!.Template;

        Assert.DoesNotContain(route, JellyfinRemoteClient.CapabilitiesPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_status_shows_the_libraries_of_each_server()
    {
        using var fixture = new RegistryFixture();
        var libraries = new Mock<IServerLibraryManager>();
        var controller = ControllerFor(fixture, libraries: libraries.Object);
        var id = (await controller.AddServer(_request, CancellationToken.None)).Value!.Id;
        var state = new AutomaticLibrariesState(true, true, [new AutomaticLibraryStatus(ServerLibraryKind.Movies, "Friend - Film")], [], AutomaticLibrariesError.None);
        libraries.Setup(manager => manager.GetState(id)).Returns(state);

        Assert.Same(state, Assert.Single(controller.GetStatus().Value!.Servers).AutomaticLibraries);
    }

    [Fact]
    public async Task Adding_a_server_switches_its_libraries_on_in_the_mode_of_new_servers()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.NewServerLibraryMode = LibraryMode.ByRemoteLibrary;
        var libraries = new Mock<IServerLibraryManager>();

        var id = (await ControllerFor(fixture, libraries: libraries.Object).AddServer(_request, CancellationToken.None)).Value!.Id;

        libraries.Verify(manager => manager.Enable(id, LibraryMode.ByRemoteLibrary), Times.Once);
    }

    [Fact]
    public async Task The_mode_of_an_added_server_is_kept_even_when_its_libraries_cannot_start_yet()
    {
        using var fixture = new RegistryFixture();
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.Enable(It.IsAny<Guid>(), It.IsAny<LibraryMode>()))
            .Returns(AutomaticLibrariesRequest.Refused(AutomaticLibrariesOutcome.NoFederatedFolder));
        var request = new AddServerRequest { Name = _request.Name, Url = _request.Url, Username = _request.Username, Password = _request.Password, LibraryMode = LibraryMode.ByRemoteLibrary };

        var id = (await ControllerFor(fixture, libraries: libraries.Object).AddServer(request, CancellationToken.None)).Value!.Id;

        Assert.Equal(LibraryMode.ByRemoteLibrary, fixture.Store.Configuration.Servers.Single(server => server.Id == id).LibraryMode);
    }

    [Fact]
    public async Task The_mode_of_the_add_form_wins_and_none_switches_nothing_on()
    {
        using var fixture = new RegistryFixture();
        var libraries = new Mock<IServerLibraryManager>();
        var request = new AddServerRequest { Name = _request.Name, Url = _request.Url, Username = _request.Username, Password = _request.Password, LibraryMode = LibraryMode.None };

        var added = await ControllerFor(fixture, libraries: libraries.Object).AddServer(request, CancellationToken.None);

        Assert.NotNull(added.Value);
        libraries.Verify(manager => manager.Enable(It.IsAny<Guid>(), It.IsAny<LibraryMode>()), Times.Never);
        libraries.Verify(manager => manager.Enable(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task An_unknown_library_mode_is_refused_before_the_server_is_added()
    {
        using var fixture = new RegistryFixture();
        var request = new AddServerRequest { Name = _request.Name, Url = _request.Url, Username = _request.Username, Password = _request.Password, LibraryMode = (LibraryMode)7 };

        var added = await ControllerFor(fixture).AddServer(request, CancellationToken.None);

        var refusal = Assert.IsType<SettingRefusal>(Assert.IsType<BadRequestObjectResult>(added.Result).Value);
        Assert.Equal(nameof(AddServerRequest.LibraryMode), refusal.Field);
        Assert.Empty(fixture.Registry.GetServers());
    }

    [Fact]
    public void Another_mode_is_refused_with_a_message()
        => Assert.NotEqual("The operation failed.", FederationAdminController.RefusalMessage(AutomaticLibrariesOutcome.ModeChangeNotAllowed));

    [Fact]
    public async Task Cancelling_a_pending_rename_answers_with_the_state()
    {
        using var fixture = new RegistryFixture();
        var renamer = new Mock<IServerRenamer>();
        var id = Guid.NewGuid();
        var state = new ServerRenameState(false, false, false);
        renamer.Setup(r => r.CancelPendingAsync(id)).ReturnsAsync(ServerRenameOutcome.Cancelled);
        renamer.Setup(r => r.GetState(id)).Returns(state);

        var result = await ControllerFor(fixture, renamer: renamer.Object).CancelServerRename(id, CancellationToken.None);

        Assert.Same(state, result.Value);
    }

    [Theory]
    [InlineData(ServerRenameOutcome.UnknownServer, 404)]
    [InlineData(ServerRenameOutcome.Busy, 409)]
    [InlineData(ServerRenameOutcome.CancelFailed, 500)]
    public async Task A_refused_cancel_answers_with_its_reason(ServerRenameOutcome outcome, int status)
    {
        using var fixture = new RegistryFixture();
        var renamer = new Mock<IServerRenamer>();
        renamer.Setup(r => r.CancelPendingAsync(It.IsAny<Guid>())).ReturnsAsync(outcome);

        var result = await ControllerFor(fixture, renamer: renamer.Object).CancelServerRename(Guid.NewGuid(), CancellationToken.None);

        var refused = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(status, refused.StatusCode);
        var body = Assert.IsType<ServerRenameRefusal>(refused.Value);
        Assert.Equal(outcome, body.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
    }

    [Fact]
    public async Task The_status_shows_the_rename_of_each_server_and_whether_a_restart_is_needed()
    {
        using var fixture = new RegistryFixture();
        var renamer = new Mock<IServerRenamer>();
        var controller = ControllerFor(fixture, renamer: renamer.Object);
        var id = (await controller.AddServer(_request, CancellationToken.None)).Value!.Id;
        var state = new ServerRenameState(false, true, false);
        renamer.Setup(r => r.GetState(id)).Returns(state);
        renamer.Setup(r => r.NeedsRestart).Returns(true);

        var status = controller.GetStatus().Value!;

        Assert.True(status.RenamedLibrariesNeedRestart);
        Assert.Same(state, Assert.Single(status.Servers).Rename);
    }

    // Q359: the page warns by server name when a rename is held up by a detach, with no path in the status.
    [Fact]
    public async Task The_status_says_when_the_rename_of_a_server_is_stuck_on_a_detach()
    {
        using var fixture = new RegistryFixture();
        var renamer = new Mock<IServerRenamer>();
        var controller = ControllerFor(fixture, renamer: renamer.Object);
        var id = (await controller.AddServer(_request, CancellationToken.None)).Value!.Id;
        renamer.Setup(r => r.GetState(id)).Returns(new ServerRenameState(false, true, false) { Stuck = true });

        var json = ToJson(controller.GetStatus().Value);

        Assert.Contains("\"Rename\":{\"Busy\":false,\"Pending\":true,\"Failed\":false,\"Stuck\":true}", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".strm", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSecretReachesTheXmlTheLogsOrTheApi()
    {
        using var fixture = new RegistryFixture();
        var controller = ControllerFor(fixture);

        var added = await controller.AddServer(_request, CancellationToken.None);
        var id = added.Value!.Id;
        var test = await controller.TestServer(id, CancellationToken.None);
        var form = new ServerEditRequest { Name = added.Value.Name, Url = added.Value.Url, Username = TestValues.Username, Password = TestValues.Password };
        var libraries = await controller.CheckServerEdit(id, form, CancellationToken.None);
        var status = controller.GetStatus();

        var api = ToJson(added.Value) + ToJson(test.Value) + ToJson(libraries.Value) + ToJson(status.Value);
        AssertNoSecret(api);
        Assert.DoesNotContain(TestValues.Username, api, StringComparison.Ordinal);
        Assert.DoesNotContain("EncryptedCredentials", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Store.Configuration.Servers[0].EncryptedCredentials, api, StringComparison.Ordinal);

        var xml = ToXml(fixture.Store.Configuration);
        AssertNoSecret(xml);
        Assert.DoesNotContain(TestValues.Username, xml, StringComparison.Ordinal);

        var logs = string.Join('\n', fixture.Logger.Lines);
        AssertNoSecret(logs);
        Assert.NotEmpty(fixture.Logger.Lines);
    }

    [Fact]
    public async Task AnAddedServerGetsItsFoldersAtOnce()
    {
        using var fixture = new RegistryFixture();
        var sync = new Mock<ICatalogSyncService>();
        var controller = CreateController(fixture, sync.Object);

        var added = await controller.AddServer(_request, CancellationToken.None);

        Assert.NotNull(added.Value);
        sync.Verify(service => service.PrepareServerFolders(), Times.Once);
    }

    private static StubHandler RemoteWithServerId() => new(request => request.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"
        ? StubHandler.Json("{\"ServerId\":\"8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b\"," + TestValues.LoginJson[1..])
        : StubHandler.StockServer_Respond(request));

    [Fact]
    public async Task ARemoteAlreadyConfiguredIsRefusedWithTheNameItHasHere()
    {
        using var fixture = new RegistryFixture(RemoteWithServerId());
        var sync = new Mock<ICatalogSyncService>();
        var controller = CreateController(fixture, sync.Object);
        await controller.AddServer(_request, CancellationToken.None);
        sync.Invocations.Clear();

        var again = await controller.AddServer(new AddServerRequest { Name = "Again", Url = TestValues.Url, Username = TestValues.Username, Password = TestValues.Password }, CancellationToken.None);

        var refused = Assert.IsType<ObjectResult>(again.Result);
        Assert.Equal(409, refused.StatusCode);
        var body = Assert.IsType<FailureResponse>(refused.Value);
        Assert.Equal(RemoteFailure.AlreadyConfigured, body.Failure);
        Assert.Equal("Friend", body.ServerName);
        Assert.Equal("This server is already configured as “Friend”.", body.Message);
        Assert.DoesNotContain(TestValues.Host, ToJson(body), StringComparison.Ordinal);
        sync.Verify(service => service.PrepareServerFolders(), Times.Never);
    }

    [Fact]
    public async Task ARemovedServerAddedAgainIsAddedAsANewServer()
    {
        using var fixture = new RegistryFixture(RemoteWithServerId());
        var controller = ControllerFor(fixture);
        var first = (await controller.AddServer(_request, CancellationToken.None)).Value!;
        await fixture.Registry.RemoveAsync(first.Id);

        var again = await controller.AddServer(new AddServerRequest { Name = "Renamed", Url = TestValues.Url, Username = TestValues.Username, Password = TestValues.Password }, CancellationToken.None);

        Assert.NotEqual(first.Id, again.Value!.Id);
        Assert.Equal("Renamed", again.Value.Name);
        Assert.DoesNotContain("Recognized", ToJson(again.Value), StringComparison.Ordinal);
        Assert.Equal(again.Value.Id, controller.GetStatus().Value!.Servers.Single().Id);
    }

    [Fact]
    public async Task AServerThatCannotBeAddedGetsNoFolders()
    {
        using var fixture = new RegistryFixture(new StubHandler(_ => throw new HttpRequestException("Connection refused")));
        var sync = new Mock<ICatalogSyncService>();
        var controller = CreateController(fixture, sync.Object);

        await controller.AddServer(_request, CancellationToken.None);

        sync.Verify(service => service.PrepareServerFolders(), Times.Never);
    }

    [Fact]
    public async Task FailuresExposeNoAddressOrSecret()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("Connection refused (" + TestValues.Host + ":8096)"));
        using var fixture = new RegistryFixture(handler);
        var controller = ControllerFor(fixture);

        var result = await controller.AddServer(_request, CancellationToken.None);

        var failed = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, failed.StatusCode);
        var text = ToJson(failed.Value) + string.Join('\n', fixture.Logger.Lines);
        Assert.DoesNotContain(TestValues.Host, text, StringComparison.Ordinal);
        AssertNoSecret(text);
    }

    [Fact]
    public async Task ACallFailingAfterTheAddLogsNoAddress()
    {
        var failing = false;
        var handler = new StubHandler(request => failing
            ? throw new HttpRequestException("No route to " + TestValues.Host)
            : StubHandler.StockServer_Respond(request));
        using var fixture = new RegistryFixture(handler);
        var server = await fixture.AddServerAsync();
        failing = true;

        var result = await ControllerFor(fixture).TestServer(server.Id, CancellationToken.None);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        var logs = string.Join('\n', fixture.Logger.Lines);
        Assert.Contains("Unreachable", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(TestValues.Host, logs, StringComparison.Ordinal);
        Assert.Equal("Offline", ControllerFor(fixture).GetStatus().Value!.Servers.Single().Connection);
    }

    [Theory]
    [InlineData(null, KeyState.Missing)]
    [InlineData("short", KeyState.Missing)]
    public async Task AnUnusableKeyIsReportedAndRefusesTheAdd(string? key, KeyState expected)
    {
        using var fixture = new RegistryFixture(key: key);
        var controller = ControllerFor(fixture);

        var status = controller.GetStatus().Value!;
        var add = await controller.AddServer(_request, CancellationToken.None);

        Assert.Equal(expected, status.KeyState);
        Assert.True(status.Suspended);
        Assert.Contains("key file", status.Warning, StringComparison.Ordinal);
        Assert.Equal(409, Assert.IsType<ObjectResult>(add.Result).StatusCode);
        Assert.Empty(fixture.Store.Configuration.Servers);
    }

    [Fact]
    public async Task AWrongKeyIsReportedAndNothingIsSent()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.Handler.Requests.Clear();
        fixture.KeySource.Key = TestValues.OtherKey;
        var controller = ControllerFor(fixture);

        var status = controller.GetStatus().Value!;
        var test = await controller.TestServer(server.Id, CancellationToken.None);

        Assert.Equal(KeyState.Wrong, status.KeyState);
        Assert.True(status.Suspended);
        Assert.NotNull(status.Warning);
        Assert.DoesNotContain(TestValues.OtherKey, status.Warning, StringComparison.Ordinal);
        Assert.Equal(409, Assert.IsType<ObjectResult>(test.Result).StatusCode);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task AWrongKeyMarksTheServerAndTheNewPasswordClearsIt()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.KeySource.Key = TestValues.OtherKey;
        var controller = ControllerFor(fixture);
        Assert.True(controller.GetStatus().Value!.Servers.Single().NeedsPassword);

        var form = new ServerEditRequest { Name = server.Name, Url = server.Url, Username = TestValues.Username, Password = TestValues.Password };
        var result = await controller.SaveServerEdit(server.Id, form, CancellationToken.None);

        Assert.False(result.Value!.NeedsPassword);
        var status = controller.GetStatus().Value!;
        Assert.Equal(KeyState.Ok, status.KeyState);
        Assert.False(status.Suspended);
        Assert.DoesNotContain(TestValues.Password, System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void AGoodKeyShowsNoWarning()
    {
        using var fixture = new RegistryFixture();

        var status = ControllerFor(fixture).GetStatus().Value!;

        Assert.Equal(KeyState.Ok, status.KeyState);
        Assert.False(status.Suspended);
        Assert.Null(status.Warning);
    }

    [Fact]
    public void WithNoFolderNothingIsChecked()
    {
        using var fixture = new RegistryFixture();
        var checker = new RecordingChecker(FederatedFolderLinkState.NotLinked);

        var folder = ControllerFor(fixture, checker).GetStatus().Value!.FederatedFolder;

        Assert.Equal(FederatedFolderLinkState.NotChecked, folder.Link);
        Assert.False(checker.Called);
    }

    [Theory]
    [InlineData("/federata", true)]
    [InlineData("relative/path", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void TheFolderSaysWhetherAUsableVariableSetsIt(string? federatedRoot, bool expected)
    {
        using var fixture = new RegistryFixture();

        var folder = ControllerFor(fixture, federatedRoot: federatedRoot).GetStatus().Value!.FederatedFolder;

        Assert.Equal(expected, folder.FromEnvironment);
    }

    [Fact]
    public async Task TheFolderShowsThePathsOfEachServerAndTheCheckerDecidesTheLink()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        var server = await fixture.AddServerAsync();
        var checker = new RecordingChecker(FederatedFolderLinkState.NotLinked);

        var folder = ControllerFor(fixture, checker).GetStatus().Value!.FederatedFolder;

        Assert.Equal([server.Id], checker.Servers);
        var shown = Assert.Single(folder.Servers);
        Assert.Equal(server.Id, shown.Id);
        Assert.Equal("Friend", shown.Name);
        Assert.Equal($"/federata/{server.Id:D}/Film", shown.MoviesPath);
        Assert.Equal($"/federata/{server.Id:D}/Serie", shown.SeriesPath);
        Assert.Equal(["Film"], shown.Movies.Libraries);
        Assert.Equal(["Serie"], shown.SeriesGroupingOff);
        Assert.Equal(["Serie"], shown.SeriesLibraries);
        Assert.Equal(FederatedFolderLinkState.NotLinked, folder.Link);
        Assert.Equal(["Tutto"], folder.Covering);
        Assert.True(folder.SeriesSplit);
    }

    [Fact]
    public async Task ALibraryOnTheFoldersOfAServerOutsideItsAutomaticOnesIsUnregistered()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        var server = await fixture.AddServerAsync();
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.GetState(server.Id))
            .Returns(new AutomaticLibrariesState(true, false, [new AutomaticLibraryStatus(ServerLibraryKind.Movies, "Friend - Film")], [], AutomaticLibrariesError.None));
        var checker = new RecordingChecker(FederatedFolderLinkState.Linked) { OnlyOnItsFolders = ["Friend - Film", "Mia"] };

        var folder = ControllerFor(fixture, checker, libraries: libraries.Object).GetStatus().Value!.FederatedFolder;

        Assert.Equal(["Mia"], Assert.Single(folder.Servers).Unregistered);
    }

    [Fact]
    public async Task AServerSavedTwiceShowsOnceWithItsFirstName()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        var server = await fixture.AddServerAsync();
        var saved = fixture.Store.Configuration.Servers[0];
        fixture.Store.Configuration.Servers.Add(new RemoteServerConfig { Id = server.Id, Name = "Copia", Url = saved.Url, EncryptedCredentials = saved.EncryptedCredentials });
        var checker = new RecordingChecker(FederatedFolderLinkState.Linked);

        var status = ControllerFor(fixture, checker).GetStatus().Value!;

        Assert.Equal(server.Name, Assert.Single(status.Servers).Name);
        Assert.Equal(server.Name, Assert.Single(status.FederatedFolder.Servers).Name);
        Assert.Equal([server.Id], checker.Servers);
    }

    [Fact]
    public async Task WithTheSwitchOffNoLibraryIsUnregistered()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        await fixture.AddServerAsync();
        var checker = new RecordingChecker(FederatedFolderLinkState.Linked) { OnlyOnItsFolders = ["Mia"] };

        var folder = ControllerFor(fixture, checker).GetStatus().Value!.FederatedFolder;

        Assert.Empty(Assert.Single(folder.Servers).Unregistered);
    }

    [Fact]
    public void TheLibrariesLeftByRemovedServersAreListed()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.RemovedServerLibraries.Add(new RemovedServerLibrariesConfig { ServerId = Guid.NewGuid(), ReleasedLibraries = { "Vecchio - Film" } });
        fixture.Store.Configuration.RemovedServerLibraries.Add(new RemovedServerLibrariesConfig { ServerId = Guid.NewGuid(), ReleasedLibraries = { "Altro - Serie" } });

        var status = ControllerFor(fixture).GetStatus().Value!;

        Assert.Equal(["Vecchio - Film", "Altro - Serie"], status.ReleasedLibraries.Select(library => library.Name));
        Assert.Equal(fixture.Store.Configuration.RemovedServerLibraries.Select(held => held.ServerId), status.ReleasedLibraries.Select(library => library.ServerId));
    }

    // Row 416: each removed server whose removal waits for its watch state, by name only.
    [Fact]
    public void TheStatusListsTheRemovalsHeldForTheirWatchState()
    {
        using var fixture = new RegistryFixture();
        var keeper = new Mock<IWatchStateKeeper>();
        keeper.Setup(k => k.HeldRemovals).Returns([new HeldRemoval("Vecchio"), new HeldRemoval(null)]);
        var controller = new FederationAdminController(
            fixture.Registry,
            fixture.Vault,
            fixture.Store,
            new RecordingChecker(FederatedFolderLinkState.NotChecked),
            new CoordinatorFixture().Coordinator,
            new FixedFederatedRootSource(null),
            Mock.Of<ICatalogSyncService>(),
            new InMemoryMergeStore(),
            Mock.Of<IServerLibraryManager>(),
            new CapturingLogger<FederationAdminController>(),
            keeper: keeper.Object);

        var status = controller.GetStatus().Value!;

        Assert.Equal(["Vecchio", null], status.HeldRemovals.Select(held => held.Name));
        Assert.Contains("\"HeldRemovals\":[{\"Name\":\"Vecchio\"},{", ToJson(status), StringComparison.Ordinal);
        Assert.Empty(ControllerFor(fixture).GetStatus().Value!.HeldRemovals);
    }

    [Fact]
    public void HidingReleasedLibrariesTakesThemOffTheStatusAndLeavesTheLibrariesAlone()
    {
        using var fixture = new RegistryFixture();
        var removed = Guid.NewGuid();
        fixture.Store.Configuration.RemovedServerLibraries.Add(new RemovedServerLibrariesConfig { ServerId = removed, ReleasedLibraries = { "Vecchio - Film", "Vecchio - Serie" } });
        var libraries = new Mock<IServerLibraryManager>();
        var controller = ControllerFor(fixture, libraries: libraries.Object);

        var result = controller.HideReleasedLibraries(new HideReleasedLibrariesRequest([new ReleasedLibrary(removed, "Vecchio - Film")]));

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(["Vecchio - Serie"], controller.GetStatus().Value!.ReleasedLibraries.Select(library => library.Name));
        var held = Assert.Single(fixture.Store.Configuration.RemovedServerLibraries);
        Assert.Equal(["Vecchio - Film", "Vecchio - Serie"], held.ReleasedLibraries);
        Assert.Equal(["Vecchio - Film"], held.HiddenReleasedLibraries);
        libraries.VerifyNoOtherCalls();
    }

    [Fact]
    public void HidingAReleasedLibraryTwiceKeepsOneMark()
    {
        using var fixture = new RegistryFixture();
        var removed = Guid.NewGuid();
        fixture.Store.Configuration.RemovedServerLibraries.Add(new RemovedServerLibrariesConfig { ServerId = removed, ReleasedLibraries = { "Vecchio - Film" } });
        var controller = ControllerFor(fixture);
        var request = new HideReleasedLibrariesRequest([new ReleasedLibrary(removed, "Vecchio - Film")]);

        controller.HideReleasedLibraries(request);
        var result = controller.HideReleasedLibraries(request);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(["Vecchio - Film"], Assert.Single(fixture.Store.Configuration.RemovedServerLibraries).HiddenReleasedLibraries);
        Assert.Empty(controller.GetStatus().Value!.ReleasedLibraries);
    }

    [Theory]
    [InlineData(false, "Vecchio - Film")]
    [InlineData(true, "Vecchio - Serie")]
    [InlineData(true, "vecchio - film")]
    public void HidingAReleasedLibraryThatIsNotListedHidesNothing(bool sameServer, string name)
    {
        using var fixture = new RegistryFixture();
        var removed = Guid.NewGuid();
        fixture.Store.Configuration.RemovedServerLibraries.Add(new RemovedServerLibrariesConfig { ServerId = removed, ReleasedLibraries = { "Vecchio - Film", "Altro - Film" } });
        var controller = ControllerFor(fixture);

        var result = controller.HideReleasedLibraries(new HideReleasedLibrariesRequest(
            [new ReleasedLibrary(removed, "Altro - Film"), new ReleasedLibrary(sameServer ? removed : Guid.NewGuid(), name)]));

        Assert.Equal(404, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Empty(Assert.Single(fixture.Store.Configuration.RemovedServerLibraries).HiddenReleasedLibraries);
        Assert.Equal(2, controller.GetStatus().Value!.ReleasedLibraries.Count);
    }

    [Fact]
    public void TheFolderCarriesTheMigrationOfItsPaths()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        var status = new LayoutMigrationStatus(LayoutMigrationStep.AddPaths, [new LayoutMigrationLibrary("Film", ["/federata/Film"], ["/federata/x/Film"])], 3);
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(item => item.GetStatus("/federata")).Returns(status);

        var folder = ControllerFor(fixture, migration: migration.Object).GetStatus().Value!.FederatedFolder;

        Assert.Same(status, folder.Migration);
    }

    [Fact]
    public void TheFolderSaysWhichLibrariesChangedOnItSinceTheSync()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        var changes = new Mock<ILibraryChangeTracker>();
        var found = new LibraryChanges(["Tutto"], ["Film"]);
        changes.Setup(tracker => tracker.Compare()).Returns(found);

        var folder = ControllerFor(fixture, changes: changes.Object).GetStatus().Value!.FederatedFolder;

        Assert.Same(found, folder.Changes);
        changes.Verify(tracker => tracker.Record(), Times.Never);
    }

    [Fact]
    public void WithNoFolderThereAreNoChanges()
    {
        using var fixture = new RegistryFixture();
        var changes = new Mock<ILibraryChangeTracker>(MockBehavior.Strict);

        var folder = ControllerFor(fixture, changes: changes.Object).GetStatus().Value!.FederatedFolder;

        Assert.Equal(LibraryChanges.None, folder.Changes);
    }

    [Fact]
    public void ChoosingTheAutomaticMigrationStartsItAndShowsTheBanner()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        var status = new LayoutMigrationStatus(LayoutMigrationStep.AddPaths, [], 3) { Mode = LayoutMigrationMode.Automatic, Running = true };
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(item => item.Choose(LayoutMigrationMode.Automatic)).Returns(new LayoutMigrationChoice(LayoutMigrationChoiceOutcome.Started, Task.CompletedTask));
        migration.Setup(item => item.GetStatus("/federata")).Returns(status);

        var result = ControllerFor(fixture, migration: migration.Object).ChooseMigration(new ChooseMigrationRequest { Mode = LayoutMigrationMode.Automatic });

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        Assert.Same(status, accepted.Value);
    }

    [Fact]
    public void ChoosingTheManualMigrationShowsTheBanner()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        var status = new LayoutMigrationStatus(LayoutMigrationStep.AddPaths, [], 3) { Mode = LayoutMigrationMode.Manual };
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(item => item.Choose(LayoutMigrationMode.Manual)).Returns(new LayoutMigrationChoice(LayoutMigrationChoiceOutcome.Chosen, Task.CompletedTask));
        migration.Setup(item => item.GetStatus("/federata")).Returns(status);

        var result = ControllerFor(fixture, migration: migration.Object).ChooseMigration(new ChooseMigrationRequest { Mode = LayoutMigrationMode.Manual });

        Assert.Same(status, result.Value);
    }

    [Theory]
    [InlineData(LayoutMigrationChoiceOutcome.Busy)]
    [InlineData(LayoutMigrationChoiceOutcome.NotAllowed)]
    [InlineData(LayoutMigrationChoiceOutcome.NothingToMigrate)]
    public void ARefusedChoiceAnswersWithItsReason(LayoutMigrationChoiceOutcome outcome)
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(item => item.Choose(It.IsAny<LayoutMigrationMode>())).Returns(LayoutMigrationChoice.Refused(outcome));

        var result = ControllerFor(fixture, migration: migration.Object).ChooseMigration(new ChooseMigrationRequest { Mode = LayoutMigrationMode.Manual });

        var refused = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(409, refused.StatusCode);
        var body = Assert.IsType<LayoutMigrationRefusal>(refused.Value);
        Assert.Equal(outcome, body.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
    }

    [Fact]
    public void WithNoFolderThereIsNoChoice()
    {
        using var fixture = new RegistryFixture();
        var migration = new Mock<ILayoutMigration>(MockBehavior.Strict);

        var result = ControllerFor(fixture, migration: migration.Object).ChooseMigration(new ChooseMigrationRequest { Mode = LayoutMigrationMode.Automatic });

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Theory]
    [InlineData(LayoutMigrationChoiceOutcome.Chosen, 204)]
    [InlineData(LayoutMigrationChoiceOutcome.Started, 202)]
    public void TheSettingsSetTheModeOfTheNewServersEvenWithNoFolder(LayoutMigrationChoiceOutcome outcome, int code)
    {
        using var fixture = new RegistryFixture();
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(item => item.SetMode(LayoutMigrationMode.Automatic)).Returns(new LayoutMigrationChoice(outcome, Task.CompletedTask));

        var result = ControllerFor(fixture, migration: migration.Object).SetNewServerMode(new ChooseMigrationRequest { Mode = LayoutMigrationMode.Automatic });

        Assert.Equal(code, Assert.IsAssignableFrom<IStatusCodeActionResult>(result).StatusCode);
        migration.Verify(item => item.SetMode(LayoutMigrationMode.Automatic), Times.Once);
    }

    [Theory]
    [InlineData(LayoutMigrationChoiceOutcome.Busy)]
    [InlineData(LayoutMigrationChoiceOutcome.NotAllowed)]
    public void ARefusedModeAnswersWithItsReason(LayoutMigrationChoiceOutcome outcome)
    {
        using var fixture = new RegistryFixture();
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(item => item.SetMode(It.IsAny<LayoutMigrationMode>())).Returns(LayoutMigrationChoice.Refused(outcome));

        var result = ControllerFor(fixture, migration: migration.Object).SetNewServerMode(new ChooseMigrationRequest { Mode = LayoutMigrationMode.Manual });

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, refused.StatusCode);
        var body = Assert.IsType<LayoutMigrationRefusal>(refused.Value);
        Assert.Equal(outcome, body.Outcome);
        Assert.Equal(FederationAdminController.MigrationRefusalMessage(outcome), body.Message);
    }

    [Fact]
    public void WithoutTheMigrationThereIsNoModeToSet()
    {
        using var fixture = new RegistryFixture();

        var result = ControllerFor(fixture).SetNewServerMode(new ChooseMigrationRequest { Mode = LayoutMigrationMode.Automatic });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void TheStatusCarriesTheModeOfTheNewServers()
    {
        using var fixture = new RegistryFixture();
        var migration = new Mock<ILayoutMigration>();
        migration.Setup(item => item.NewServerMode).Returns(LayoutMigrationMode.Automatic);

        Assert.Equal(LayoutMigrationMode.Automatic, ControllerFor(fixture, migration: migration.Object).GetStatus().Value!.FederatedFolder.NewServerMode);
        Assert.Equal(LayoutMigrationMode.Manual, ControllerFor(fixture).GetStatus().Value!.FederatedFolder.NewServerMode);
    }

    [Fact]
    public void WithNoFolderThereIsNoMigration()
    {
        using var fixture = new RegistryFixture();
        var migration = new Mock<ILayoutMigration>();

        var folder = ControllerFor(fixture, migration: migration.Object).GetStatus().Value!.FederatedFolder;

        Assert.Null(folder.Migration);
        migration.Verify(item => item.GetStatus(It.IsAny<string>()), Times.Never);
    }

    private sealed class RecordingChecker : IFederatedFolderLinkChecker
    {
        private readonly FederatedFolderLinkState _state;

        public RecordingChecker(FederatedFolderLinkState state) => _state = state;

        public bool Called { get; private set; }

        public IReadOnlyList<Guid> Servers { get; private set; } = [];

        public IReadOnlyList<string> OnlyOnItsFolders { get; init; } = [];

        public FederatedFolderLinkReport Check(string federatedFolderPath, IReadOnlyList<Guid> serverIds)
        {
            Called = true;
            Servers = serverIds;
            if (_state == FederatedFolderLinkState.NotChecked)
            {
                return FederatedFolderLinkReport.NotChecked;
            }

            var link = _state == FederatedFolderLinkState.Linked ? FederatedPathLink.Linked : FederatedPathLink.NotLinked;
            var servers = serverIds
                .Select(id => new ServerFolderLink(
                    id,
                    FederatedFolderLayout.ServerMoviesPath(federatedFolderPath, id),
                    FederatedFolderLayout.ServerSeriesPath(federatedFolderPath, id),
                    new(link, ["Film"]),
                    new(link, []),
                    ["Serie"],
                    ["Serie"],
                    OnlyOnItsFolders))
                .ToList();
            return new FederatedFolderLinkReport(servers, ["Tutto"], true);
        }
    }

    [Fact]
    public void TheSharedLibrariesShowTheirState()
    {
        using var fixture = new RegistryFixture();
        var state = new SharedLibrariesState(true, false, [new AutomaticLibraryStatus(ServerLibraryKind.Movies, "Tutti i server - Film")], [], AutomaticLibrariesError.None)
        {
            Conflicts = [new LibraryConflict("Tutto", LibraryConflictReason.ParentPath, ServerLibraryKind.Series, string.Empty, ["/federata/x/Serie"])]
        };
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.GetSharedState()).Returns(state);

        Assert.Same(state, ControllerFor(fixture, libraries: libraries.Object).GetSharedLibraries().Value);
    }

    [Fact]
    public void SwitchingTheSharedLibrariesOnStartsWithNoConfirmation()
    {
        using var fixture = new RegistryFixture();
        var state = new SharedLibrariesState(true, true, [], [], AutomaticLibrariesError.None);
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.EnableShared()).Returns(new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.Started, Task.CompletedTask, []));
        libraries.Setup(manager => manager.GetSharedState()).Returns(state);

        var result = ControllerFor(fixture, libraries: libraries.Object).SetSharedLibraries(new SharedLibrariesRequest { Enabled = true }).Result;

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Same(state, accepted.Value);
        libraries.Verify(manager => manager.DisableShared(It.IsAny<SharedLibrariesConfirmation?>()), Times.Never);
    }

    [Fact]
    public void SwitchingTheSharedLibrariesOffAsksToConfirmWithTheLibrariesItWouldDeleteAndRelease()
    {
        using var fixture = new RegistryFixture();
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.DisableShared(null))
            .Returns(new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.ConfirmationRequired, Task.CompletedTask, ["Tutti i server - Film"]) { ToRelease = ["Serie di tutti"] });

        var result = ControllerFor(fixture, libraries: libraries.Object).SetSharedLibraries(new SharedLibrariesRequest { Enabled = false }).Result;

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);
        var refusal = Assert.IsType<SharedLibrariesRefusal>(conflict.Value);
        Assert.Equal(AutomaticLibrariesOutcome.ConfirmationRequired, refusal.Outcome);
        Assert.Equal("Confirm to delete the shared libraries the plugin made and release the adopted ones.", refusal.Message);
        Assert.Equal(["Tutti i server - Film"], refusal.ToDelete);
        Assert.Equal(["Serie di tutti"], refusal.ToRelease);
    }

    [Fact]
    public void AConfirmedSwitchOffStartsTheDeletion()
    {
        using var fixture = new RegistryFixture();
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.DisableShared(It.IsAny<SharedLibrariesConfirmation>())).Returns(new AutomaticLibrariesRequest(AutomaticLibrariesOutcome.Started, Task.CompletedTask, []));

        var result = ControllerFor(fixture, libraries: libraries.Object)
            .SetSharedLibraries(new SharedLibrariesRequest { Enabled = false, Confirmed = true, ToDelete = ["Tutti i server - Film"], ToRelease = ["Serie di tutti"] }).Result;

        Assert.IsType<AcceptedResult>(result);
        libraries.Verify(
            manager => manager.DisableShared(It.Is<SharedLibrariesConfirmation>(confirmation =>
                confirmation.ToDelete.SequenceEqual(new[] { "Tutti i server - Film" }) && confirmation.ToRelease.SequenceEqual(new[] { "Serie di tutti" }))),
            Times.Once);
    }

    [Theory]
    [InlineData(AutomaticLibrariesOutcome.NoFederatedFolder, "Set the federated folder first.")]
    [InlineData(AutomaticLibrariesOutcome.Busy, "The shared libraries are being updated. Try again when it is done.")]
    public void ARefusedSwitchOnSaysWhy(AutomaticLibrariesOutcome outcome, string message)
    {
        using var fixture = new RegistryFixture();
        var libraries = new Mock<IServerLibraryManager>();
        libraries.Setup(manager => manager.EnableShared()).Returns(AutomaticLibrariesRequest.Refused(outcome));

        var result = ControllerFor(fixture, libraries: libraries.Object).SetSharedLibraries(new SharedLibrariesRequest { Enabled = true }).Result;

        var refusal = Assert.IsType<SharedLibrariesRefusal>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal((outcome, message), (refusal.Outcome, refusal.Message));
    }
}
