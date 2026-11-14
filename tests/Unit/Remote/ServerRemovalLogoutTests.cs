using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

// Decision 413: removing a server ends its login on the remote, within a short time, and never fails for it.
public class ServerRemovalLogoutTests
{
    private const string LoginPath = "/Users/AuthenticateByName";
    private const string LogoutPath = "/Sessions/Logout";

    private static readonly RemoteCredentials OtherCredentials = new() { Username = "other-user", Password = "other-pass" };

    private static StubHandler Remote(HttpStatusCode logout = HttpStatusCode.NoContent) => new(request => request.RequestUri!.AbsolutePath == LogoutPath
        ? StubHandler.Status(logout)
        : StubHandler.StockServer_Respond(request));

    private static Task<RemoteResult<int>> PingAsync(RemoteServerRegistry registry, Guid id)
        => registry.RunWithSessionAsync(id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);

    private static void AssertClean(CapturingLogger<RemoteServerRegistry> logger)
    {
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain(TestValues.Token, line, StringComparison.Ordinal);
            Assert.DoesNotContain("tailnet", line, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task RemovalLogsOutTheLiveLoginWithItsTokenAndDevice()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();

        Assert.True(await fixture.Registry.RemoveAsync(server.Id));

        var logout = Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == LogoutPath);
        Assert.Equal(HttpMethod.Post, logout.Method);
        Assert.Contains($"Token=\"{TestValues.Token}\"", logout.Authorization, StringComparison.Ordinal);
        Assert.Contains($"DeviceId=\"jellymesh-{server.Id:N}\"", logout.Authorization, StringComparison.Ordinal);
        Assert.Empty(fixture.Registry.GetServers());
    }

    [Fact]
    public async Task AfterTheRemovalNothingLogsInAgain()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        await fixture.Registry.RemoveAsync(server.Id);
        fixture.Handler.Requests.Clear();

        var call = await PingAsync(fixture.Registry, server.Id);

        Assert.Equal(RemoteFailure.UnknownServer, call.Failure);
        Assert.Empty(fixture.Handler.Requests);
    }

    // Decision 418: after a restart there is no live login, so the removal logs in with the same device and out at once.
    [Fact]
    public async Task WithoutALiveLoginTheRemovalLogsInAndOutWithTheSameDevice()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, fixture.Client, logger);
        fixture.Handler.Requests.Clear();

        Assert.True(await restarted.RemoveAsync(server.Id));

        Assert.Equal([LoginPath, LogoutPath], fixture.Handler.Requests.Select(request => request.Uri.AbsolutePath));
        Assert.All(fixture.Handler.Requests, request => Assert.Contains($"DeviceId=\"jellymesh-{server.Id:N}\"", request.Authorization, StringComparison.Ordinal));
        Assert.Contains($"Token=\"{TestValues.Token}\"", fixture.Handler.Requests[1].Authorization, StringComparison.Ordinal);
        Assert.False(restarted.HoldsStateFor(server.Id));
        Assert.Empty(restarted.GetServers());
        Assert.Contains(logger.Lines, line => line.Contains("logging in again", StringComparison.Ordinal) && line.Contains("Friend", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("may stay listed", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TestValues.OtherKey)]
    public async Task WithoutALiveLoginAndWithCredentialsThatDoNotDecryptNothingIsSentAndTheDeviceIsSaidToStay(string? key)
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, fixture.Client, logger);
        fixture.Handler.Requests.Clear();
        fixture.KeySource.Key = key;

        Assert.True(await restarted.RemoveAsync(server.Id));

        Assert.Empty(fixture.Handler.Requests);
        Assert.Empty(restarted.GetServers());
        Assert.Contains(logger.Lines, line => line.Contains("no live login", StringComparison.Ordinal)
            && line.Contains("may stay listed", StringComparison.Ordinal)
            && line.Contains("Friend", StringComparison.Ordinal));
        Assert.All(logger.Lines, line => Assert.DoesNotContain(TestValues.Password, line, StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Theory]
    [InlineData(LoginPath)]
    [InlineData(LogoutPath)]
    public async Task WithoutALiveLoginARemoteThatHangsIsCutAtTheLimit(string hangingPath)
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        using var handler = new HangingHandler(hangingPath);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger) { LogoutLimit = TimeSpan.FromMilliseconds(200) };
        var watch = Stopwatch.StartNew();

        Assert.True(await restarted.RemoveAsync(server.Id));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.True(handler.Started);
        Assert.False(restarted.HoldsStateFor(server.Id));
        Assert.Contains(logger.Lines, line => line.Contains("Could not end the login", StringComparison.Ordinal)
            && line.Contains(nameof(RemoteFailure.Timeout), StringComparison.Ordinal)
            && line.Contains("Friend", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task WithoutALiveLoginAStoredAddressThatCannotBeReadIsLoggedAndTheRemovalStands()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        fixture.Store.Update(config => config.Servers.Single().Url = "not an address");
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, fixture.Client, logger);
        fixture.Handler.Requests.Clear();

        Assert.True(await restarted.RemoveAsync(server.Id));

        Assert.Empty(fixture.Handler.Requests);
        Assert.Empty(restarted.GetServers());
        Assert.Contains(logger.Lines, line => line.Contains("Could not end the login", StringComparison.Ordinal) && line.Contains("Friend", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task WithoutALiveLoginARefusedLoginIsLoggedAndTheRemovalStands()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, RefusingLoginClient(), logger);

        Assert.True(await restarted.RemoveAsync(server.Id));

        Assert.Empty(restarted.GetServers());
        Assert.Contains(logger.Lines, line => line.Contains("Could not end the login", StringComparison.Ordinal)
            && line.Contains(nameof(RemoteFailure.AuthenticationFailed), StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task ARefusedLogoutIsLoggedWithoutTokenOrAddressAndTheRemovalStands()
    {
        using var fixture = new RegistryFixture(Remote(HttpStatusCode.InternalServerError));
        var server = await fixture.AddServerAsync();

        Assert.True(await fixture.Registry.RemoveAsync(server.Id));

        Assert.Empty(fixture.Registry.GetServers());
        Assert.Contains(fixture.Logger.Lines, line => line.Contains("Could not end the login", StringComparison.Ordinal) && line.Contains("Friend", StringComparison.Ordinal));
        AssertClean(fixture.Logger);
    }

    [Fact]
    public async Task ALogoutThatHangsIsCutAtTheLimit()
    {
        using var handler = new HangingLogoutHandler();
        using var fixture = new RegistryFixture(Remote());
        var logger = new CapturingLogger<RemoteServerRegistry>();
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        using var registry = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger) { LogoutLimit = TimeSpan.FromMilliseconds(200) };
        var added = await registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);
        var watch = Stopwatch.StartNew();

        Assert.True(await registry.RemoveAsync(added.Value!.Id));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.True(handler.LogoutStarted);
        Assert.Contains(logger.Lines, line => line.Contains("Could not end the login", StringComparison.Ordinal) && line.Contains(nameof(RemoteFailure.Timeout), StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task ALoginRunningDuringTheRemovalIsLoggedOutWhenItEnds()
    {
        using var handler = new HeldLoginHandler();
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, new CapturingLogger<RemoteServerRegistry>()) { LogoutLimit = TimeSpan.FromMilliseconds(200) };

        var call = PingAsync(restarted, server.Id);
        await handler.LoginArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await restarted.RemoveAsync(server.Id));
        handler.ReleaseLogin.SetResult();

        Assert.Equal(RemoteFailure.UnknownServer, (await call.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
        Assert.Equal(1, handler.Logouts);
    }

    [Fact]
    public async Task ALoginForNewCredentialsThatEndsAfterTheRemovalIsLoggedOut()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, fixture.Client, logger);
        fixture.Handler.Requests.Clear();
        Task<bool>? removal = null;
        store.BeforeUpdate = () => removal = restarted.RemoveAsync(server.Id);

        var update = await restarted.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.True(await removal!);
        Assert.Equal(RemoteFailure.UnknownServer, update.Failure);
        // The update ends its login, then the removal, which found no live login, logs in and out with the same device (418).
        Assert.Equal([LoginPath, LogoutPath, LoginPath, LogoutPath], fixture.Handler.Requests.Select(request => request.Uri.AbsolutePath));
        var logout = fixture.Handler.Requests[1];
        Assert.Contains($"Token=\"{TestValues.Token}\"", logout.Authorization, StringComparison.Ordinal);
        Assert.Contains(logger.Lines, line => line.Contains("removed", StringComparison.Ordinal) && line.Contains("Friend", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task ALoginForNewCredentialsIsLoggedOutWhenTheRemovalEndsRightAfterItIsSaved()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, fixture.Client, logger);
        fixture.Handler.Requests.Clear();
        Task<bool>? removal = null;
        store.AfterUpdate = () => removal = restarted.RemoveAsync(server.Id);

        var update = await restarted.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.True(await removal!);
        Assert.Equal(RemoteFailure.UnknownServer, update.Failure);
        Assert.Equal([LoginPath, LogoutPath, LoginPath, LogoutPath], fixture.Handler.Requests.Select(request => request.Uri.AbsolutePath));
        Assert.Contains(logger.Lines, line => line.Contains("removed", StringComparison.Ordinal) && line.Contains("Friend", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task ALoginEndingRightAfterAFailedRemovalKeepsTheServerWorking()
    {
        using var handler = new HeldLoginHandler();
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, client, new CapturingLogger<RemoteServerRegistry>());
        var call = Task.Run(() => PingAsync(restarted, server.Id));
        await handler.LoginArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Store.SaveFailure = new IOException();
        store.FailedUpdate = () =>
        {
            // The login ends after the failed save, which has put the server back already.
            handler.ReleaseLogin.SetResult();
            SpinWait.SpinUntil(() => call.IsCompleted, TimeSpan.FromSeconds(5));
            fixture.Store.SaveFailure = null;
        };

        await Assert.ThrowsAsync<IOException>(() => restarted.RemoveAsync(server.Id));

        Assert.True((await call.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(0, handler.Logouts);
        Assert.Single(restarted.GetServers());
        Assert.True((await PingAsync(restarted, server.Id)).IsSuccess);
    }

    [Fact]
    public async Task ACredentialsUpdateWaitsForARunningLoginAndKeepsItsOwnToken()
    {
        using var handler = new IssuingLoginHandler();
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger);
        var call = PingAsync(restarted, server.Id);
        await handler.FirstLoginArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Runs up to its first pending wait: without the gate its login would reach the remote now.
        var update = restarted.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(1, handler.Logins);
        Assert.False(update.IsCompleted);
        handler.ReleaseFirstLogin.SetResult();
        Assert.True((await call.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.True((await update.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(2, handler.Logins);
        var used = await restarted.RunWithSessionAsync(server.Id, (_, session, _) => Task.FromResult(RemoteResult.Success(session.AccessToken)), CancellationToken.None);
        Assert.Equal(handler.Live, used.Value);
        Assert.Equal(2, handler.Logins);
        AssertClean(logger);
    }

    [Fact]
    public async Task AfterARunningLoginAndACredentialsUpdateTheRemovalLogsOutTheLiveLogin()
    {
        using var handler = new IssuingLoginHandler();
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger);
        var call = PingAsync(restarted, server.Id);
        await handler.FirstLoginArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var update = restarted.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);
        handler.ReleaseFirstLogin.SetResult();
        await call.WaitAsync(TimeSpan.FromSeconds(5));
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        var lastIssued = handler.Live;

        Assert.True(await restarted.RemoveAsync(server.Id));

        Assert.NotNull(lastIssued);
        Assert.Equal([lastIssued], handler.LoggedOut);
        Assert.Null(handler.Live);
        AssertClean(logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALoginEndedForARemovedServerAlsoLogsOutTheLoginItsServerKept(bool remoteRefusesRevokedTokens)
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false, refuseRevoked: remoteRefusesRevokedTokens);
        using var fixture = new RegistryFixture(Remote());
        var store = new HookedStore(fixture.Store);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(store, fixture.Vault, client, logger);
        var server = (await registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None)).Value!;
        var added = handler.Live;
        var removalSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? removal = null;
        store.BeforeUpdate = () =>
        {
            // The update saves after the removal is saved and ends before the removal takes the server's login.
            store.AfterUpdate = () =>
            {
                removalSaved.SetResult();
                SpinWait.SpinUntil(() => updateEnded.Task.IsCompleted, TimeSpan.FromSeconds(5));
            };
            removal = Task.Run(() => registry.RemoveAsync(server.Id));
            SpinWait.SpinUntil(() => removalSaved.Task.IsCompleted, TimeSpan.FromSeconds(5));
        };

        var update = await Task.Run(async () =>
        {
            try
            {
                return await registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);
            }
            finally
            {
                updateEnded.SetResult();
            }
        });

        Assert.True(await removal!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(RemoteFailure.UnknownServer, update.Failure);
        Assert.NotNull(added);
        Assert.Equal(3, handler.Logins);
        // The update took the server's runtime, so the removal finds no live login and logs in and out once more (418).
        Assert.Equal([added, TestValues.Token + "2", TestValues.Token + "3"], handler.LoggedOut.Order(StringComparer.Ordinal));
        Assert.Null(handler.Live);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("stays listed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("Could not end", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALoginThatRacesTheAddAfterItIsSavedIsTheOneKeptAndTheRemovalLogsItOut(bool remoteRefusesRevokedTokens)
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false, refuseRevoked: remoteRefusesRevokedTokens);
        using var fixture = new RegistryFixture(Remote());
        var store = new HookedStore(fixture.Store);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(store, fixture.Vault, client, logger);
        Task<RemoteResult<int>>? call = null;
        store.AfterUpdate = () =>
        {
            // A background call logs in after the new server is saved and before the add keeps its own login.
            var id = registry.GetServers().Single().Id;
            call = Task.Run(() => PingAsync(registry, id));
            SpinWait.SpinUntil(() => call.IsCompleted, TimeSpan.FromSeconds(5));
        };

        var added = await registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);

        Assert.True(added.IsSuccess);
        Assert.True((await call!.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(2, handler.Logins);
        var used = await registry.RunWithSessionAsync(added.Value!.Id, (_, session, _) => Task.FromResult(RemoteResult.Success(session.AccessToken)), CancellationToken.None);
        Assert.Equal(handler.Live, used.Value);
        Assert.True(await registry.RemoveAsync(added.Value.Id));

        // The add ends its own login, which a remote may already have revoked, and the removal the kept one.
        Assert.Equal([TestValues.Token + "1", TestValues.Token + "2"], handler.LoggedOut);
        Assert.Null(handler.Live);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("Could not end", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARemovalRightAfterTheAddIsSavedLogsOutTheLoginOfTheAdd(bool remoteRefusesRevokedTokens)
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false, refuseRevoked: remoteRefusesRevokedTokens);
        using var fixture = new RegistryFixture(Remote());
        var store = new HookedStore(fixture.Store);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(store, fixture.Vault, client, logger);
        Task<bool>? removal = null;
        store.AfterUpdate = () => removal = registry.RemoveAsync(registry.GetServers().Single().Id);

        var added = await registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);

        Assert.True(await removal!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(RemoteFailure.UnknownServer, added.Failure);
        // The removal runs before the add holds a runtime: it logs in and out, then the add ends its own login.
        Assert.Equal([TestValues.Token + "2", TestValues.Token + "1"], handler.LoggedOut);
        Assert.Null(handler.Live);
        Assert.Empty(registry.GetServers());
        Assert.DoesNotContain(logger.Lines, line => line.Contains("stays listed", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains("Could not end", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task ACredentialsLoginThatFailsAfterTheRemovalLeavesNoStateForTheServer()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, RefusingLoginClient(), new CapturingLogger<RemoteServerRegistry>());
        Task<bool>? removal = null;
        store.AfterRead = () => removal = restarted.RemoveAsync(server.Id);

        var update = await restarted.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.True(await removal!);
        Assert.Equal(RemoteFailure.AuthenticationFailed, update.Failure);
        Assert.False(restarted.HoldsStateFor(server.Id));
    }

    [Fact]
    public async Task ACancelledCredentialsUpdateAfterTheRemovalLeavesNoStateForTheServer()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, fixture.Client, new CapturingLogger<RemoteServerRegistry>());
        Task<bool>? removal = null;
        store.AfterRead = () => removal = restarted.RemoveAsync(server.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted.UpdateCredentialsAsync(server.Id, TestValues.Credentials, new CancellationToken(true)));

        Assert.True(await removal!);
        Assert.False(restarted.HoldsStateFor(server.Id));
    }

    [Fact]
    public async Task ACancelledCredentialsUpdateDuringTheRemovalLeavesTheLiveLoginToIt()
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false);
        using var fixture = new RegistryFixture(Remote());
        var store = new HookedStore(fixture.Store);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        using var registry = new RemoteServerRegistry(store, fixture.Vault, client, new CapturingLogger<RemoteServerRegistry>());
        var server = (await registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None)).Value!;
        var removalSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updateEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? removal = null;
        store.AfterRead = () =>
        {
            // The update ends after the removal is saved and before the removal takes the server's login.
            store.AfterUpdate = () =>
            {
                removalSaved.SetResult();
                SpinWait.SpinUntil(() => updateEnded.Task.IsCompleted, TimeSpan.FromSeconds(5));
            };
            removal = Task.Run(() => registry.RemoveAsync(server.Id));
            SpinWait.SpinUntil(() => removalSaved.Task.IsCompleted, TimeSpan.FromSeconds(5));
        };

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, new CancellationToken(true)));
        }
        finally
        {
            updateEnded.SetResult();
        }

        Assert.True(await removal!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([TestValues.Token + "1"], handler.LoggedOut);
        Assert.Null(handler.Live);
    }

    [Fact]
    public async Task ACallWhoseLoginFailsAfterTheRemovalLeavesNoStateForTheServer()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, RefusingLoginClient(), new CapturingLogger<RemoteServerRegistry>());
        Task<bool>? removal = null;

        // The removal starts once the call has read its credentials under the login gate.
        store.AfterRead = () => store.AfterRead = () => removal = restarted.RemoveAsync(server.Id);

        var call = await PingAsync(restarted, server.Id);

        Assert.True(await removal!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(RemoteFailure.AuthenticationFailed, call.Failure);
        Assert.False(restarted.HoldsStateFor(server.Id));
    }

    // Decision 419: new credentials that log in but cannot be saved end that login, and the kept one is left alone.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewCredentialsThatCannotBeSavedLogOutTheirLoginAndLeaveTheKeptOne(bool saveFails)
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false);
        using var fixture = new RegistryFixture(Remote());
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger);
        var server = (await registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None)).Value!;
        var stored = fixture.Store.Read(config => config.Servers.Single().EncryptedCredentials);
        handler.OnLogin = () =>
        {
            // After the login, the key goes or the save fails.
            if (saveFails)
            {
                fixture.Store.SaveFailure = new IOException();
            }
            else
            {
                fixture.KeySource.Key = null;
            }
        };

        if (saveFails)
        {
            await Assert.ThrowsAsync<IOException>(() => registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None));
        }
        else
        {
            Assert.Equal(RemoteFailure.Suspended, (await registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None)).Failure);
        }

        Assert.Equal([TestValues.Token + "2"], handler.LoggedOut);
        handler.OnLogin = null;
        fixture.Store.SaveFailure = null;
        fixture.KeySource.Key = TestValues.Key;
        Assert.Equal(stored, fixture.Store.Read(config => config.Servers.Single().EncryptedCredentials));

        var used = await registry.RunWithSessionAsync(server.Id, (_, session, _) => Task.FromResult(RemoteResult.Success(session.AccessToken)), CancellationToken.None);
        Assert.Equal(TestValues.Token + "1", used.Value);
        AssertClean(logger);
    }

    [Fact]
    public async Task NewCredentialsThatCannotBeSavedLeaveTheStoredOnesForTheNextLogin()
    {
        const string RemoteId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";
        var withServerId = false;
        InMemoryConfigurationStore? store = null;
        using var fixture = new RegistryFixture(new StubHandler(request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case LoginPath when withServerId:
                    // The login of the new credentials returns a Server Id, then the save fails.
                    store!.SaveFailure = new IOException();
                    return StubHandler.Json(TestValues.LoginJson.Replace("{\"AccessToken\"", "{\"ServerId\":\"" + RemoteId + "\",\"AccessToken\"", StringComparison.Ordinal));
                case LogoutPath:
                    return StubHandler.Status(HttpStatusCode.NoContent);
                default:
                    return StubHandler.StockServer_Respond(request);
            }
        }));
        store = fixture.Store;
        var server = await fixture.AddServerAsync();
        var stored = fixture.Store.Read(config => config.Servers.Single().EncryptedCredentials);
        withServerId = true;

        await Assert.ThrowsAsync<IOException>(() => fixture.Registry.UpdateCredentialsAsync(server.Id, new RemoteCredentials { Username = "other-user", Password = "other-pass" }, CancellationToken.None));

        Assert.Equal(stored, fixture.Store.Read(config => config.Servers.Single().EncryptedCredentials));
        Assert.Equal(string.Empty, fixture.Store.Read(config => config.Servers.Single().JellyfinServerId));
        Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == LogoutPath);

        // The kept login gets a 401 at its next call and logs in again with the stored credentials.
        withServerId = false;
        fixture.Store.SaveFailure = null;
        fixture.Handler.Requests.Clear();
        var attempts = 0;
        var call = await fixture.Registry.RunWithSessionAsync(
            server.Id,
            (_, _, _) => Task.FromResult(++attempts == 1 ? RemoteResult.Fail<int>(RemoteFailure.AuthenticationFailed) : RemoteResult.Success(1)),
            CancellationToken.None);

        Assert.True(call.IsSuccess);
        var login = Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == LoginPath);
        Assert.Contains(TestValues.Username, login.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("other-user", login.Body, StringComparison.Ordinal);
        Assert.All(fixture.Logger.Lines, line => Assert.DoesNotContain("other-pass", line, StringComparison.Ordinal));
        AssertClean(fixture.Logger);
    }

    // A call that reads the server right after the failed save waits for the gate, then logs in with the saved credentials.
    [Fact]
    public async Task ACallRightAfterAFailedSaveOfNewCredentialsLogsInWithTheSavedOnes()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, fixture.Client, logger);
        fixture.Store.SaveFailure = new IOException();
        var before = 0;
        var read = false;
        Task<RemoteResult<int>>? call = null;
        store.FailedUpdate = () =>
        {
            before = fixture.Handler.Requests.Count;
            store.AfterRead = () => Volatile.Write(ref read, true);
            call = Task.Run(() => PingAsync(restarted, server.Id));
            SpinWait.SpinUntil(() => Volatile.Read(ref read), TimeSpan.FromSeconds(5));
        };

        await Assert.ThrowsAsync<IOException>(() => restarted.UpdateCredentialsAsync(server.Id, OtherCredentials, CancellationToken.None));

        Assert.True((await call!.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        var login = Assert.Single(fixture.Handler.Requests.Skip(before), request => request.Uri.AbsolutePath == LoginPath);
        Assert.Contains(TestValues.Username, login.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherCredentials.Username, login.Body, StringComparison.Ordinal);
        AssertClean(logger);
    }

    // Decision 418 after a failed save of new credentials: the removed server carries the saved credentials.
    [Fact]
    public async Task ARemovalRightAfterAFailedSaveOfNewCredentialsEndsTheDeviceWithTheSavedOnes()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, fixture.Client, logger);
        fixture.Store.SaveFailure = new IOException();
        var before = 0;
        Task<bool>? removal = null;
        store.FailedUpdate = () =>
        {
            // The removal is saved, then waits for the login gate the change holds.
            before = fixture.Handler.Requests.Count;
            fixture.Store.SaveFailure = null;
            removal = Task.Run(() => restarted.RemoveAsync(server.Id));
            SpinWait.SpinUntil(() => fixture.Store.Read(config => config.Servers.Count == 0), TimeSpan.FromSeconds(5));
        };

        await Assert.ThrowsAsync<IOException>(() => restarted.UpdateCredentialsAsync(server.Id, OtherCredentials, CancellationToken.None));

        Assert.True(await removal!.WaitAsync(TimeSpan.FromSeconds(5)));
        var login = Assert.Single(fixture.Handler.Requests.Skip(before), request => request.Uri.AbsolutePath == LoginPath);
        Assert.Contains(TestValues.Username, login.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherCredentials.Username, login.Body, StringComparison.Ordinal);
        Assert.Empty(restarted.GetServers());
        AssertClean(logger);
    }

    // A removal whose save also fails puts the server back with the saved credentials, whatever ends first.
    [Fact]
    public async Task AFailedRemovalRightAfterAFailedSaveOfNewCredentialsKeepsTheSavedOnes()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var stored = fixture.Store.Read(config => config.Servers.Single().EncryptedCredentials);
        var store = new HookedStore(fixture.Store);
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, fixture.Client, logger);
        fixture.Store.SaveFailure = new IOException();
        var removalFailed = false;
        Task? change = null;
        Task<bool>? removal = null;
        store.FailedUpdate = () =>
        {
            // The removal fails to save too, and goes on only once the change of credentials has ended.
            store.FailedUpdate = () =>
            {
                Volatile.Write(ref removalFailed, true);
                SpinWait.SpinUntil(() => Volatile.Read(ref change)?.IsCompleted == true, TimeSpan.FromSeconds(5));
            };
            removal = Task.Run(() => restarted.RemoveAsync(server.Id));
            SpinWait.SpinUntil(() => Volatile.Read(ref removalFailed), TimeSpan.FromSeconds(5));
        };

        Volatile.Write(ref change, Task.Run(() => restarted.UpdateCredentialsAsync(server.Id, OtherCredentials, CancellationToken.None)));

        await Assert.ThrowsAsync<IOException>(() => change!);
        await Assert.ThrowsAsync<IOException>(() => removal!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(stored, fixture.Store.Read(config => config.Servers.Single().EncryptedCredentials));
        AssertClean(logger);
    }

    // The login reads the credentials under the login gate, not from a copy taken before it.
    [Fact]
    public async Task ALoginUsesTheCredentialsStoredWhenItTakesTheGate()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, fixture.Client, logger);
        var other = fixture.Vault.Protect(server.Id, OtherCredentials)!;
        store.AfterRead = () => fixture.Store.Configuration.Servers.Single().EncryptedCredentials = other;
        fixture.Handler.Requests.Clear();

        Assert.True((await PingAsync(restarted, server.Id)).IsSuccess);

        var login = Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == LoginPath);
        Assert.Contains(OtherCredentials.Username, login.Body, StringComparison.Ordinal);
        AssertClean(logger);
    }

    // As above, for the login again after a 401.
    [Fact]
    public async Task ALoginAfterA401UsesTheCredentialsStoredWhenItTakesTheGate()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var other = fixture.Vault.Protect(server.Id, OtherCredentials)!;
        fixture.Handler.Requests.Clear();
        var attempts = 0;

        var call = await fixture.Registry.RunWithSessionAsync(
            server.Id,
            (_, _, _) =>
            {
                if (++attempts > 1)
                {
                    return Task.FromResult(RemoteResult.Success(1));
                }

                fixture.Store.Configuration.Servers.Single().EncryptedCredentials = other;
                return Task.FromResult(RemoteResult.Fail<int>(RemoteFailure.AuthenticationFailed));
            },
            CancellationToken.None);

        Assert.True(call.IsSuccess);
        var login = Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == LoginPath);
        Assert.Contains(OtherCredentials.Username, login.Body, StringComparison.Ordinal);
        AssertClean(fixture.Logger);
    }

    [Fact]
    public async Task AnAddWhoseCredentialsCannotBeEncryptedLogsOutItsLogin()
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false);
        using var fixture = new RegistryFixture(Remote());
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger);
        handler.OnLogin = () => fixture.KeySource.Key = null;

        var added = await registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.Suspended, added.Failure);
        Assert.Equal([TestValues.Token + "1"], handler.LoggedOut);
        Assert.Null(handler.Live);
        Assert.Empty(registry.GetServers());
        AssertClean(logger);
    }

    [Fact]
    public async Task AnAddThatCannotBeSavedLogsOutItsLoginAndKeepsNoServer()
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false);
        using var fixture = new RegistryFixture(Remote());
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger);
        handler.OnLogin = () => fixture.Store.SaveFailure = new IOException();

        await Assert.ThrowsAsync<IOException>(() => registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None));

        Assert.Equal([TestValues.Token + "1"], handler.LoggedOut);
        Assert.Null(handler.Live);
        Assert.Empty(registry.GetServers());
        Assert.DoesNotContain(logger.Lines, line => line.Contains("Could not end", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, line => line.Contains("Friend", StringComparison.Ordinal) && line.Contains(nameof(IOException), StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task AnAddThatCannotBeSavedCutsTheLogoutOfItsLoginAtTheLimit()
    {
        using var fixture = new RegistryFixture(Remote());
        using var handler = new HangingHandler(LogoutPath) { OnLogin = () => fixture.Store.SaveFailure = new IOException() };
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger) { LogoutLimit = TimeSpan.FromMilliseconds(200) };
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<IOException>(() => registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.True(handler.Started);
        Assert.Empty(registry.GetServers());
        Assert.Contains(logger.Lines, line => line.Contains("Friend", StringComparison.Ordinal) && line.Contains(nameof(RemoteFailure.Timeout), StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task AnAddThatCannotBeSavedIsOutOfMemoryBeforeAnyCallCanSeeIt()
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false);
        using var fixture = new RegistryFixture(Remote());
        var store = new HookedStore(fixture.Store);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(store, fixture.Vault, client, logger);
        fixture.Store.SaveFailure = new IOException();
        var seen = -1;
        store.FailedUpdate = () => seen = registry.GetServers().Count;

        await Assert.ThrowsAsync<IOException>(() => registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None));

        Assert.Equal(0, seen);
        Assert.Equal(1, handler.Logins);
        Assert.Equal([TestValues.Token + "1"], handler.LoggedOut);
        Assert.Null(handler.Live);
        Assert.Empty(registry.GetServers());
        Assert.DoesNotContain(logger.Lines, line => line.Contains("Could not end", StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task NewCredentialsThatCannotBeSavedCutTheLogoutOfTheirLoginAtTheLimit()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        using var handler = new HangingHandler(LogoutPath) { OnLogin = () => fixture.KeySource.Key = null };
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var registry = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, logger) { LogoutLimit = TimeSpan.FromMilliseconds(200) };
        var watch = Stopwatch.StartNew();

        var update = await registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.Equal(RemoteFailure.Suspended, update.Failure);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.True(handler.Started);
        Assert.Contains(logger.Lines, line => line.Contains("Friend", StringComparison.Ordinal) && line.Contains(nameof(RemoteFailure.Timeout), StringComparison.Ordinal));
        AssertClean(logger);
    }

    [Fact]
    public async Task ACancelledCredentialsUpdateDoesNotDropARuntimeThatKeptALoginMeanwhile()
    {
        using var handler = new IssuingLoginHandler();
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var store = new HookedStore(fixture.Store);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(handler)) { Delay = (_, _) => Task.CompletedTask };
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(store, fixture.Vault, client, logger);
        var call = PingAsync(restarted, server.Id);
        await handler.FirstLoginArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var update = restarted.UpdateCredentialsAsync(server.Id, TestValues.Credentials, cancel.Token);
        var removalSaved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? removal = null;
        store.BeforeRead = () =>
        {
            // While the cancelled update lets go of the server: the call keeps its login, then the removal is saved.
            handler.ReleaseFirstLogin.SetResult();
            SpinWait.SpinUntil(() => call.IsCompleted, TimeSpan.FromSeconds(5));
            store.AfterUpdate = () =>
            {
                removalSaved.SetResult();
                proceed.Task.Wait(TimeSpan.FromSeconds(5));
            };
            removal = Task.Run(() => restarted.RemoveAsync(server.Id));
            SpinWait.SpinUntil(() => removalSaved.Task.IsCompleted, TimeSpan.FromSeconds(5));
        };

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5)));
        proceed.SetResult();

        Assert.True((await call.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.True(await removal!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Logins);
        Assert.Equal([TestValues.Token + "1"], handler.LoggedOut);
        Assert.Null(handler.Live);
        Assert.False(restarted.HoldsStateFor(server.Id));
        AssertClean(logger);
    }

    private static JellyfinRemoteClient RefusingLoginClient() => new(new StubHttpClientFactory(new StubHandler(request => request.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"
        ? StubHandler.Status(HttpStatusCode.Unauthorized)
        : StubHandler.StockServer_Respond(request))))
    {
        Delay = (_, _) => Task.CompletedTask
    };

    // Runs a step once, just before or just after the next change of the configuration, when its save fails, or around a read.
    private sealed class HookedStore(InMemoryConfigurationStore inner) : IPluginConfigurationStore
    {
        public event EventHandler? SettingsChanged
        {
            add => inner.SettingsChanged += value;
            remove => inner.SettingsChanged -= value;
        }

        public Action? BeforeUpdate { get; set; }

        public Action? AfterUpdate { get; set; }

        public Action? FailedUpdate { get; set; }

        public Action? AfterRead { get; set; }

        public Action? BeforeRead { get; set; }

        public T Read<T>(Func<PluginConfiguration, T> read)
        {
            var before = BeforeRead;
            BeforeRead = null;
            before?.Invoke();
            var result = inner.Read(read);
            var after = AfterRead;
            AfterRead = null;
            after?.Invoke();
            return result;
        }

        public void Update(Action<PluginConfiguration> update) => Update(() => inner.Update(update));

        // The rollback runs in the inner store, so FailedUpdate sees the configuration already put back.
        public void Update(Action<PluginConfiguration> update, Action<PluginConfiguration> rollback) => Update(() => inner.Update(update, rollback));

        private void Update(Action change)
        {
            var before = BeforeUpdate;
            BeforeUpdate = null;
            before?.Invoke();
            try
            {
                change();
            }
            catch
            {
                var failed = FailedUpdate;
                FailedUpdate = null;
                failed?.Invoke();
                throw;
            }

            var after = AfterUpdate;
            AfterUpdate = null;
            after?.Invoke();
        }
    }

    private sealed class HangingLogoutHandler : HttpMessageHandler
    {
        public bool LogoutStarted { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == LogoutPath)
            {
                LogoutStarted = true;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return StubHandler.StockServer_Respond(request);
        }
    }

    // A stock remote whose answers on one path never come.
    private sealed class HangingHandler(string path) : HttpMessageHandler
    {
        private int _started;

        public bool Started => Volatile.Read(ref _started) == 1;

        public Action? OnLogin { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == LoginPath)
            {
                OnLogin?.Invoke();
            }

            if (request.RequestUri!.AbsolutePath == path)
            {
                Volatile.Write(ref _started, 1);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return request.RequestUri.AbsolutePath == LogoutPath ? StubHandler.Status(HttpStatusCode.NoContent) : StubHandler.StockServer_Respond(request);
        }
    }

    private sealed class HeldLoginHandler : HttpMessageHandler
    {
        private int _logouts;

        public TaskCompletionSource LoginArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Logouts => Volatile.Read(ref _logouts);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case LoginPath:
                    LoginArrived.TrySetResult();
                    await ReleaseLogin.Task.WaitAsync(cancellationToken);
                    break;
                case LogoutPath:
                    Interlocked.Increment(ref _logouts);
                    return StubHandler.Status(HttpStatusCode.NoContent);
            }

            return StubHandler.StockServer_Respond(request);
        }
    }

    // A remote that issues a new token at each login, revoking the earlier one, and can hold the answer to the first login.
    // It can also refuse the logout of a revoked token, as Jellyfin does.
    private sealed class IssuingLoginHandler(bool holdFirstLogin = true, bool refuseRevoked = false) : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<string> _loggedOut = [];
        private int _logins;
        private string? _live;

        public TaskCompletionSource FirstLoginArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstLogin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Runs at each login, before its answer.
        public Action? OnLogin { get; set; }

        public int Logins
        {
            get
            {
                lock (_gate)
                {
                    return _logins;
                }
            }
        }

        public string? Live
        {
            get
            {
                lock (_gate)
                {
                    return _live;
                }
            }
        }

        public IReadOnlyList<string> LoggedOut
        {
            get
            {
                lock (_gate)
                {
                    return _loggedOut.ToList();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case LoginPath:
                    string token;
                    bool first;
                    lock (_gate)
                    {
                        _logins++;
                        token = TestValues.Token + _logins.ToString(CultureInfo.InvariantCulture);
                        _live = token;
                        first = holdFirstLogin && _logins == 1;
                    }

                    OnLogin?.Invoke();
                    if (first)
                    {
                        FirstLoginArrived.TrySetResult();
                        await ReleaseFirstLogin.Task.WaitAsync(cancellationToken);
                    }

                    return StubHandler.Json(TestValues.LoginJson.Replace(TestValues.Token, token, StringComparison.Ordinal));
                case LogoutPath:
                    var sent = TokenOf(request);
                    bool revoked;
                    lock (_gate)
                    {
                        _loggedOut.Add(sent);
                        revoked = _live != sent;
                        if (!revoked)
                        {
                            _live = null;
                        }
                    }

                    return StubHandler.Status(refuseRevoked && revoked ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent);
                default:
                    return StubHandler.StockServer_Respond(request);
            }
        }

        private static string TokenOf(HttpRequestMessage request)
        {
            var header = request.Headers.GetValues("Authorization").Single();
            var start = header.IndexOf("Token=\"", StringComparison.Ordinal) + "Token=\"".Length;
            return header[start..header.IndexOf('"', start)];
        }
    }
}
