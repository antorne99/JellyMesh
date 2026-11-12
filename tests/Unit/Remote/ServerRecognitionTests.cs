using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

// Decisions 116, 120, 121 and 122: a remote is known by the Server Id it gives itself at login.
public class ServerRecognitionTests
{
    private const string FirstId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";
    private const string OtherId = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";

    private string? _serverId = FirstId;

    private StubHandler Remote() => new(request => request.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"
        ? StubHandler.Json(_serverId is null ? TestValues.LoginJson : "{\"ServerId\":\"" + _serverId + "\"," + TestValues.LoginJson[1..])
        : StubHandler.StockServer_Respond(request));

    private static Task<RemoteResult<RemoteServerInfo>> AddAsync(RegistryFixture fixture, string name)
        => fixture.Registry.AddAsync(name, TestValues.Url, TestValues.Credentials, CancellationToken.None);

    private static Task<RemoteResult<int>> PingAsync(RegistryFixture fixture, Guid id)
        => fixture.Registry.RunWithSessionAsync(id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);

    private static string DeviceOf(StubHandler.RecordedRequest request)
        => request.Authorization!.Split("DeviceId=\"")[1].Split('"')[0];

    [Fact]
    public async Task AddStoresTheServerIdOfTheRemote()
    {
        using var fixture = new RegistryFixture(Remote());

        await fixture.AddServerAsync();

        Assert.Equal(FirstId, fixture.Store.Configuration.Servers.Single().JellyfinServerId);
    }

    [Fact]
    public async Task ARemovedServerAddedAgainGetsItsIdBackAndSaysWhatItWasCalled()
    {
        using var fixture = new RegistryFixture(Remote());
        var first = await fixture.AddServerAsync();
        fixture.Registry.Remove(first.Id);

        var again = await AddAsync(fixture, "Renamed");

        Assert.True(again.IsSuccess, again.Failure.ToString());
        Assert.Equal(first.Id, again.Value!.Id);
        Assert.Equal("Friend", again.Value.RecognizedAs);
        Assert.Equal("Renamed", again.Value.Name);
        Assert.Empty(fixture.Store.Configuration.FormerServers);
        Assert.Equal(TestValues.Password, fixture.Vault.Unprotect(first.Id, fixture.Store.Configuration.Servers.Single().EncryptedCredentials)!.Password);
    }

    [Fact]
    public async Task AServerAddedAgainUsesItsOwnDeviceAndEndsTheFirstLogin()
    {
        using var fixture = new RegistryFixture(Remote());
        var first = await fixture.AddServerAsync();
        fixture.Registry.Remove(first.Id);
        fixture.Handler.Requests.Clear();

        await AddAsync(fixture, "Friend");
        await PingAsync(fixture, first.Id);

        var logins = fixture.Handler.Requests.Where(request => request.Uri.AbsolutePath == "/Users/AuthenticateByName").ToList();
        var logout = Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
        Assert.Equal(2, logins.Count);
        Assert.Equal(HttpMethod.Post, logout.Method);
        Assert.Equal(DeviceOf(logins[0]), DeviceOf(logout));
        Assert.NotEqual($"jellymesh-{first.Id:N}", DeviceOf(logins[0]));
        Assert.Equal($"jellymesh-{first.Id:N}", DeviceOf(logins[1]));
        Assert.All(
            fixture.Handler.Requests.Where(request => request.Uri.AbsolutePath != "/Sessions/Logout").Skip(1),
            request => Assert.Equal($"jellymesh-{first.Id:N}", DeviceOf(request)));
    }

    [Fact]
    public async Task AnotherRemoteGetsANewIdWhileTheRemovedOneStaysKnown()
    {
        using var fixture = new RegistryFixture(Remote());
        var first = await fixture.AddServerAsync();
        fixture.Registry.Remove(first.Id);
        _serverId = OtherId;

        var other = await AddAsync(fixture, "Friend");

        Assert.NotEqual(first.Id, other.Value!.Id);
        Assert.Null(other.Value.RecognizedAs);
        var former = Assert.Single(fixture.Store.Configuration.FormerServers);
        Assert.Equal((first.Id, FirstId, "Friend"), (former.Id, former.JellyfinServerId, former.Name));
    }

    [Fact]
    public async Task ARemoteAlreadyConfiguredIsRefusedWithTheNameItHasHere()
    {
        using var fixture = new RegistryFixture(Remote());
        var first = await fixture.AddServerAsync();
        var saves = fixture.Store.Saves;

        var again = await AddAsync(fixture, "Again");

        Assert.Equal(RemoteFailure.AlreadyConfigured, again.Failure);
        Assert.Equal(first.Id, again.Value!.Id);
        Assert.Equal("Friend", again.Value.Name);
        Assert.Single(fixture.Store.Configuration.Servers);
        Assert.Equal(saves, fixture.Store.Saves);
        Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
    }

    [Fact]
    public async Task ARemoteWithNoServerIdIsNeitherRecognizedNorRefused()
    {
        _serverId = null;
        using var fixture = new RegistryFixture(Remote());
        var first = await fixture.AddServerAsync();
        fixture.Registry.Remove(first.Id);

        var second = await AddAsync(fixture, "Friend");
        var third = await AddAsync(fixture, "Friend");

        Assert.NotEqual(first.Id, second.Value!.Id);
        Assert.NotEqual(second.Value.Id, third.Value!.Id);
        Assert.Empty(fixture.Store.Configuration.FormerServers);
        Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
    }

    [Fact]
    public async Task AFailedRemovalKeepsNoRecordOfTheServer()
    {
        using var fixture = new RegistryFixture(Remote());
        var first = await fixture.AddServerAsync();
        fixture.Store.SaveFailure = new System.IO.IOException("disk full");

        Assert.Throws<System.IO.IOException>(() => fixture.Registry.Remove(first.Id));

        Assert.Single(fixture.Store.Configuration.Servers);
        Assert.Empty(fixture.Store.Configuration.FormerServers);
    }

    [Fact]
    public async Task AServerStoredWithoutItsIdGetsItAtItsNextLogin()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        fixture.Store.Configuration.Servers.Single().JellyfinServerId = string.Empty;
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, fixture.Client, fixture.Logger);
        var saves = fixture.Store.Saves;

        await restarted.RunWithSessionAsync(server.Id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);
        await restarted.RunWithSessionAsync(server.Id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);

        Assert.Equal(FirstId, fixture.Store.Configuration.Servers.Single().JellyfinServerId);
        Assert.Equal(saves + 1, fixture.Store.Saves);
    }

    [Fact]
    public async Task AServerStoredWithoutItsIdGetsItWhenItsLoginIsEnteredAgain()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        fixture.Store.Configuration.Servers.Single().JellyfinServerId = string.Empty;

        await fixture.Registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(FirstId, fixture.Store.Configuration.Servers.Single().JellyfinServerId);
    }

    [Fact]
    public async Task AStoredIdIsNotReplacedByAnotherAtLogin()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, fixture.Client, fixture.Logger);
        _serverId = OtherId;

        await restarted.RunWithSessionAsync(server.Id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);

        Assert.Equal(FirstId, fixture.Store.Configuration.Servers.Single().JellyfinServerId);
    }

    [Fact]
    public async Task AnIdThatCannotBeSavedAtLoginLeavesTheCallWorking()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        fixture.Store.Configuration.Servers.Single().JellyfinServerId = string.Empty;
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, fixture.Client, fixture.Logger);
        fixture.Store.SaveFailure = new System.IO.IOException("disk full " + TestValues.Url);

        var result = await restarted.RunWithSessionAsync(server.Id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(fixture.Logger.Lines, line => line.Contains(TestValues.Host, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARecordWhoseServerIsConfiguredAgainRefusesTheAdd()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var stored = fixture.Store.Configuration.Servers.Single();
        stored.JellyfinServerId = string.Empty;
        fixture.Store.Configuration.FormerServers.Add(new FormerServerConfig { Id = server.Id, JellyfinServerId = FirstId, Name = "Old" });

        var again = await AddAsync(fixture, "Again");

        Assert.Equal(RemoteFailure.AlreadyConfigured, again.Failure);
        Assert.Equal("Friend", again.Value!.Name);
        Assert.Single(fixture.Store.Configuration.Servers);
    }
}
