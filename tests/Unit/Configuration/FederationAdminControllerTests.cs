using System;
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
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    private static FederationAdminController ControllerFor(RegistryFixture fixture, IFederatedFolderLinkChecker? checker = null, string? federatedRoot = null)
        => new(
            fixture.Registry,
            fixture.Vault,
            fixture.Store,
            checker ?? new RecordingChecker(FederatedFolderLinkState.NotChecked),
            new CoordinatorFixture().Coordinator,
            new FixedFederatedRootSource(federatedRoot),
            Mock.Of<ICatalogSyncService>());

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
            sync.Object);

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
            sync.Object);

        var result = await controller.RemoveServer(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        sync.Verify(service => service.StartRemovalCleanup(), Times.Never);
        sync.Verify(service => service.RemoveServerAsync(It.IsAny<Guid>(), It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_removal_that_cannot_be_saved_keeps_the_server_and_says_to_try_again()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var sync = new Mock<ICatalogSyncService>();
        sync.Setup(service => service.RemoveServerAsync(server.Id, It.IsAny<Func<Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, Func<Task<bool>>, CancellationToken>((_, remove, _) => remove());
        var controller = new FederationAdminController(
            fixture.Registry,
            fixture.Vault,
            fixture.Store,
            new RecordingChecker(FederatedFolderLinkState.NotChecked),
            new CoordinatorFixture().Coordinator,
            new FixedFederatedRootSource(null),
            sync.Object);
        fixture.Store.SaveFailure = new IOException("disk full");

        var result = Assert.IsType<ObjectResult>(await controller.RemoveServer(server.Id, CancellationToken.None));

        Assert.Equal(500, result.StatusCode);
        var failure = Assert.IsType<FailureResponse>(result.Value);
        Assert.Equal(RemoteFailure.RemovalFailed, failure.Failure);
        Assert.Contains("Try again", failure.Message, StringComparison.Ordinal);
        Assert.Equal(server.Id, Assert.Single(fixture.Registry.GetServers()).Id);
        sync.Verify(service => service.StartRemovalCleanup(), Times.Never);
    }

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
    public async Task NoSecretReachesTheXmlTheLogsOrTheApi()
    {
        using var fixture = new RegistryFixture();
        var controller = ControllerFor(fixture);

        var added = await controller.AddServer(_request, CancellationToken.None);
        var id = added.Value!.Id;
        var test = await controller.TestServer(id, CancellationToken.None);
        var libraries = await controller.GetLibraries(id, CancellationToken.None);
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

        var result = await controller.UpdateCredentials(server.Id, new CredentialsRequest { Username = TestValues.Username, Password = TestValues.Password }, CancellationToken.None);

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
    public async Task LibrariesAreMarkedWhenSelected()
    {
        using var fixture = new RegistryFixture();
        var controller = ControllerFor(fixture);
        var server = await fixture.AddServerAsync();

        Assert.IsType<NoContentResult>(controller.SetLibraries(server.Id, new SetLibrariesRequest(["2"])));
        var libraries = await controller.GetLibraries(server.Id, CancellationToken.None);

        Assert.Equal([false, true], libraries.Value!.Select(library => library.Selected));
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
    public void TheFolderShowsItsPathsAndTheCheckerDecidesTheLink()
    {
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.FederatedFolderPath = "/federata";

        var folder = ControllerFor(fixture, new RecordingChecker(FederatedFolderLinkState.NotLinked)).GetStatus().Value!.FederatedFolder;

        Assert.Equal("/federata/Film", folder.MoviesPath);
        Assert.Equal("/federata/Serie", folder.SeriesPath);
        Assert.Equal(FederatedFolderLinkState.NotLinked, folder.Link);
        Assert.Equal(["Film"], folder.Movies.Libraries);
        Assert.Equal(["Serie"], folder.SeriesGroupingOff);
    }

    private sealed class RecordingChecker : IFederatedFolderLinkChecker
    {
        private readonly FederatedFolderLinkState _state;

        public RecordingChecker(FederatedFolderLinkState state) => _state = state;

        public bool Called { get; private set; }

        public FederatedFolderLinkReport Check(string federatedFolderPath)
        {
            Called = true;
            var link = _state switch
            {
                FederatedFolderLinkState.Linked => FederatedPathLink.Linked,
                FederatedFolderLinkState.NotLinked => FederatedPathLink.NotLinked,
                _ => FederatedPathLink.NotChecked
            };
            return new FederatedFolderLinkReport(new(link, ["Film"]), new(link, []), ["Serie"]);
        }
    }
}
