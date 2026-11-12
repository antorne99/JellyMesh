using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

public class RemoteServerRegistryTests
{
    private static Task<RemoteResult<int>> PingAsync(RegistryFixture fixture, Guid id)
        => fixture.Registry.RunWithSessionAsync(id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);

    [Fact]
    public async Task AddLogsInDetectsStandardAndStoresEncrypted()
    {
        using var fixture = new RegistryFixture();

        var server = await fixture.AddServerAsync();

        Assert.Equal(RemoteMode.Standard, server.Mode);
        var stored = Assert.Single(fixture.Store.Configuration.Servers);
        Assert.Equal(TestValues.Url, stored.Url);
        Assert.Equal(TestValues.Password, fixture.Vault.Unprotect(stored.Id, stored.EncryptedCredentials)!.Password);
        Assert.DoesNotContain(TestValues.Password, stored.EncryptedCredentials, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachServerGetsItsOwnStableDeviceId()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();

        Assert.All(fixture.Handler.Requests, request =>
            Assert.Contains($"DeviceId=\"jellymesh-{server.Id:N}\"", request.Authorization, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RemoteFailure.AuthenticationFailed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RemoteFailure.ServerError)]
    public async Task AFailedLoginSavesNothing(HttpStatusCode status, RemoteFailure expected)
    {
        using var fixture = new RegistryFixture(new StubHandler(_ => StubHandler.Status(status)));

        var result = await fixture.Registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(expected, result.Failure);
        Assert.Empty(fixture.Store.Configuration.Servers);
        Assert.Equal(0, fixture.Store.Saves);
    }

    [Fact]
    public async Task AnAdministratorAccountIsNotStored()
    {
        using var fixture = new RegistryFixture(new StubHandler(_ => StubHandler.Json(
            "{\"AccessToken\":\"t\",\"User\":{\"Id\":\"u\",\"Policy\":{\"IsAdministrator\":true}}}")));

        var result = await fixture.Registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.AdministratorAccount, result.Failure);
        Assert.Empty(fixture.Store.Configuration.Servers);
    }

    [Theory]
    [InlineData("", TestValues.Url)]
    [InlineData("Friend", "not a url")]
    [InlineData("Friend", "http://user:pw@server")]
    public async Task InvalidInputSendsNothing(string name, string url)
    {
        using var fixture = new RegistryFixture();

        var result = await fixture.Registry.AddAsync(name, url, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.InvalidServer, result.Failure);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too short")]
    public async Task WithoutAUsableKeyAddSavesAndSendsNothing(string? key)
    {
        using var fixture = new RegistryFixture(key: key);

        var result = await fixture.Registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.Suspended, result.Failure);
        Assert.Empty(fixture.Handler.Requests);
        Assert.Empty(fixture.Store.Configuration.Servers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("too short")]
    [InlineData(TestValues.OtherKey)]
    public async Task AnUnusableKeySuspendsEveryRemoteOperationWithoutSendingAnything(string? key)
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.Handler.Requests.Clear();
        fixture.KeySource.Key = key;

        var test = await fixture.Registry.TestAsync(server.Id, CancellationToken.None);
        var libraries = await fixture.Registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        var called = false;
        var run = await fixture.Registry.RunWithSessionAsync(
            server.Id,
            (_, _, _) =>
            {
                called = true;
                return Task.FromResult(RemoteResult.Success(1));
            },
            CancellationToken.None);

        Assert.Equal(RemoteFailure.Suspended, test.Failure);
        Assert.Equal(RemoteFailure.Suspended, libraries.Failure);
        Assert.Equal(RemoteFailure.Suspended, run.Failure);
        Assert.False(called, "A session cached before the key changed must not be used.");
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ServersCanBeRemovedWithAWrongKeyAndThatClearsTheState()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.KeySource.Key = TestValues.OtherKey;
        Assert.Equal(KeyState.Wrong, fixture.Vault.GetKeyState());

        Assert.True(fixture.Registry.Remove(server.Id));

        Assert.Empty(fixture.Registry.GetServers());
        Assert.Equal(KeyState.Ok, fixture.Vault.GetKeyState());
    }

    [Fact]
    public async Task RemovingAServerDuringACallDoesNotBreakTheCall()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();

        var result = await fixture.Registry.RunWithSessionAsync(
            server.Id,
            (_, _, _) =>
            {
                fixture.Registry.Remove(server.Id);
                return Task.FromResult(RemoteResult.Fail<int>(RemoteFailure.AuthenticationFailed));
            },
            CancellationToken.None);

        Assert.Equal(RemoteFailure.AuthenticationFailed, result.Failure);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task AServerWhoseRemovalCannotBeSavedStays(Type failure)
    {
        using var fixture = new RegistryFixture();
        var first = await fixture.AddServerAsync();
        var server = await fixture.AddServerAsync();
        var last = await fixture.AddServerAsync();
        fixture.Store.SaveFailure = (Exception)Activator.CreateInstance(failure)!;

        Assert.Throws(failure, () => fixture.Registry.Remove(server.Id));

        Assert.Equal([first.Id, server.Id, last.Id], fixture.Registry.GetServers().Select(item => item.Id));
        fixture.Store.SaveFailure = null;
        Assert.True(fixture.Registry.Remove(server.Id));
    }

    [Fact]
    public async Task RemovingAServerHoldsItsAutomaticLibrariesForDeletion()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var record = new AutomaticLibraryConfig { Kind = ServerLibraryKind.Series, Name = "Casa - Serie", ItemId = "b2" };
        fixture.Store.Update(config =>
        {
            var stored = config.Servers.Single();
            stored.AutomaticLibraries = true;
            stored.AutomaticLibraryRegistry.Add(record);
            stored.PreexistingLibraryIds.Add("c3");
        });

        Assert.True(fixture.Registry.Remove(server.Id));

        var held = Assert.Single(fixture.Store.Configuration.RemovedServerLibraries);
        Assert.Equal(server.Id, held.ServerId);
        Assert.Equal(("Casa - Serie", "b2"), (Assert.Single(held.Libraries).Name, held.Libraries[0].ItemId));
        Assert.Equal(["c3"], held.PreexistingLibraryIds);
    }

    [Fact]
    public async Task RemovingAServerWithItsSwitchOffHoldsNothing()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();

        Assert.True(fixture.Registry.Remove(server.Id));

        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
    }

    [Fact]
    public async Task ARemovalThatCannotBeSavedHoldsNoAutomaticLibrary()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.Store.Update(config =>
        {
            var stored = config.Servers.Single();
            stored.AutomaticLibraries = true;
            stored.AutomaticLibraryRegistry.Add(new AutomaticLibraryConfig { Kind = ServerLibraryKind.Movies, Name = "Casa - Film" });
        });
        fixture.Store.SaveFailure = new IOException("disk full");

        Assert.Throws<IOException>(() => fixture.Registry.Remove(server.Id));

        Assert.Empty(fixture.Store.Configuration.RemovedServerLibraries);
        Assert.Single(Assert.Single(fixture.Store.Configuration.Servers).AutomaticLibraryRegistry);
    }

    [Fact]
    public void RemovingAnUnknownServerReturnsFalse()
    {
        using var fixture = new RegistryFixture();

        Assert.False(fixture.Registry.Remove(Guid.NewGuid()));
    }

    [Fact]
    public async Task TheSessionFromTheLoginIsReusedWithoutLoggingInAgain()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();

        await fixture.Registry.TestAsync(server.Id, CancellationToken.None);
        await fixture.Registry.GetLibrariesAsync(server.Id, CancellationToken.None);

        Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task AfterARestartTheStoredCredentialsLogIn()
    {
        using var first = new RegistryFixture();
        var server = await first.AddServerAsync();
        using var restarted = new RegistryFixture();
        restarted.Store.Configuration.Servers.Add(first.Store.Configuration.Servers.Single());

        var result = await restarted.Registry.GetLibrariesAsync(server.Id, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Contains(TestValues.Password, restarted.Handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedTokenLogsInAgainOnce()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.Handler.Requests.Clear();
        var calls = 0;

        var result = await fixture.Registry.RunWithSessionAsync(
            server.Id,
            (_, _, _) => Task.FromResult(++calls == 1
                ? RemoteResult.Fail<int>(RemoteFailure.AuthenticationFailed)
                : RemoteResult.Success(calls)),
            CancellationToken.None);

        Assert.Equal(2, result.Value);
        Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task AClosedRouteCostsNoLogin()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/AuthenticateByName" => StubHandler.Json(TestValues.LoginJson),
            "/UserViews" or "/JellyMesh/Provider/Libraries" => StubHandler.Status(HttpStatusCode.Forbidden),
            _ when request.RequestUri.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal) => StubHandler.Status(HttpStatusCode.Forbidden),
            _ when request.RequestUri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal) => StubHandler.Status(HttpStatusCode.Forbidden),
            _ => StubHandler.Status(HttpStatusCode.NotFound)
        });
        using var fixture = new RegistryFixture(handler);
        var server = await fixture.AddServerAsync();
        handler.Requests.Clear();
        var playback = new RemotePlaybackClient(new StubHttpClientFactory(handler));

        var libraries = await fixture.Registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        var playlist = await fixture.Registry.RunWithSessionAsync(
            server.Id,
            (endpoint, session, token) => playback.ReadPlaylistAsync(endpoint, session, "/x.m3u8", token),
            CancellationToken.None);
        var info = await fixture.Registry.RunWithSessionAsync(
            server.Id,
            (endpoint, session, token) => playback.GetPlaybackInfoAsync(endpoint, session, new RemotePlaybackRequest { ItemId = "i", MediaSourceId = "m" }, token),
            CancellationToken.None);

        Assert.Equal(RemoteFailure.Forbidden, libraries.Failure);
        Assert.Equal(RemoteFailure.Forbidden, playlist.Failure);
        Assert.Equal(RemoteFailure.Forbidden, info.Failure);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task ATokenRefusedTwiceStopsThere()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        var calls = 0;

        var result = await fixture.Registry.RunWithSessionAsync(
            server.Id,
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult(RemoteResult.Fail<int>(RemoteFailure.AuthenticationFailed));
            },
            CancellationToken.None);

        Assert.Equal(RemoteFailure.AuthenticationFailed, result.Failure);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentCallsLogInOnce()
    {
        using var first = new RegistryFixture();
        var server = await first.AddServerAsync();
        using var fixture = new RegistryFixture();
        fixture.Store.Configuration.Servers.Add(first.Store.Configuration.Servers.Single());

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => PingAsync(fixture, server.Id))));

        Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task TestRefreshesCapabilities()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.Store.Configuration.Servers[0].Mode = RemoteMode.JellyMesh;

        var result = await fixture.Registry.TestAsync(server.Id, CancellationToken.None);

        Assert.Equal(RemoteMode.Standard, result.Value!.Capabilities.Mode);
        Assert.Equal(2, result.Value.LibraryCount);
        Assert.Equal(RemoteMode.Standard, fixture.Store.Configuration.Servers[0].Mode);
    }

    [Fact]
    public async Task ACapabilityErrorLeavesTheModeUnknownNotStandard()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users/AuthenticateByName" => StubHandler.Json(TestValues.LoginJson),
            "/UserViews" => StubHandler.Json(TestValues.ViewsJson),
            _ => StubHandler.Status(HttpStatusCode.BadGateway)
        });
        using var fixture = new RegistryFixture(handler);

        var server = await fixture.AddServerAsync();

        Assert.Equal(RemoteMode.Unknown, server.Mode);
    }

    [Fact]
    public async Task ImportedLibrariesAreStoredPerServer()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();

        Assert.True(fixture.Registry.SetImportedLibraries(server.Id, ["1", "2", "1"]));

        Assert.Equal(["1", "2"], fixture.Registry.GetServers().Single().ImportedLibraryIds);
        Assert.False(fixture.Registry.SetImportedLibraries(Guid.NewGuid(), ["1"]));
    }

    [Fact]
    public async Task UnknownServersAreReported()
    {
        using var fixture = new RegistryFixture();

        var result = await fixture.Registry.TestAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(RemoteFailure.UnknownServer, result.Failure);
    }

    [Fact]
    public async Task AfterTheKeyFileIsReplacedTheServerAsksForItsPasswordAndAcceptsItAgain()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        Assert.False(server.NeedsPassword);

        fixture.KeySource.Key = TestValues.OtherKey;

        Assert.True(Assert.Single(fixture.Registry.GetServers()).NeedsPassword);
        Assert.Equal(KeyState.Wrong, fixture.Vault.GetKeyState());

        var result = await fixture.Registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Failure.ToString());
        Assert.False(Assert.Single(fixture.Registry.GetServers()).NeedsPassword);
        Assert.Equal(KeyState.Ok, fixture.Vault.GetKeyState());
        Assert.Equal(server.ImportedLibraryIds, Assert.Single(fixture.Registry.GetServers()).ImportedLibraryIds);
    }

    [Fact]
    public async Task APasswordTheServerRefusesChangesNothing()
    {
        var refuse = false;
        var handler = new StubHandler(request => refuse ? StubHandler.Status(HttpStatusCode.Unauthorized) : StubHandler.StockServer_Respond(request));
        using var fixture = new RegistryFixture(handler);
        var server = await fixture.AddServerAsync();
        var stored = fixture.Store.Configuration.Servers.Single().EncryptedCredentials;
        fixture.KeySource.Key = TestValues.OtherKey;
        refuse = true;

        var result = await fixture.Registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.AuthenticationFailed, result.Failure);
        Assert.Equal(stored, fixture.Store.Configuration.Servers.Single().EncryptedCredentials);
    }

    [Fact]
    public async Task WithoutAKeyCredentialsCannotBeEnteredAgain()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.KeySource.Key = null;

        var result = await fixture.Registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.Suspended, result.Failure);
    }

    [Fact]
    public async Task CredentialsOfAnUnknownServerAreRefused()
    {
        using var fixture = new RegistryFixture();

        var result = await fixture.Registry.UpdateCredentialsAsync(Guid.NewGuid(), TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.UnknownServer, result.Failure);
    }
}
