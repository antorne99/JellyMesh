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
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

// Decision 413: removing a server ends its login on the remote, within a short time, and never fails for it.
public class ServerRemovalLogoutTests
{
    private const string LoginPath = "/Users/AuthenticateByName";
    private const string LogoutPath = "/Sessions/Logout";

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
    public async Task ALoginEndingWhileAFailedRemovalIsUndoneLeavesTheServerWorking()
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
            // The login ends after the failed save and before the server is put back.
            handler.ReleaseLogin.SetResult();
            SpinWait.SpinUntil(() => call.IsCompleted, TimeSpan.FromSeconds(5));
            fixture.Store.SaveFailure = null;
        };

        await Assert.ThrowsAsync<IOException>(() => restarted.RemoveAsync(server.Id));

        Assert.Equal(RemoteFailure.UnknownServer, (await call.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
        Assert.Equal(1, handler.Logouts);
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

    [Fact]
    public async Task ALoginThatRacesTheAddAfterItIsSavedIsTheOneKeptAndTheRemovalLogsItOut()
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false);
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
        Assert.Equal([TestValues.Token + "2"], handler.LoggedOut);
        Assert.Null(handler.Live);
        AssertClean(logger);
    }

    [Fact]
    public async Task ARemovalRightAfterTheAddIsSavedLogsOutTheLoginOfTheAdd()
    {
        using var handler = new IssuingLoginHandler(holdFirstLogin: false);
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
        store.AfterRead = () => removal = restarted.RemoveAsync(server.Id);

        var call = await PingAsync(restarted, server.Id);

        Assert.True(await removal!);
        Assert.Equal(RemoteFailure.AuthenticationFailed, call.Failure);
        Assert.False(restarted.HoldsStateFor(server.Id));
    }

    private static JellyfinRemoteClient RefusingLoginClient() => new(new StubHttpClientFactory(new StubHandler(request => request.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"
        ? StubHandler.Status(HttpStatusCode.Unauthorized)
        : StubHandler.StockServer_Respond(request))))
    {
        Delay = (_, _) => Task.CompletedTask
    };

    // Runs a step once, just before or just after the next change of the configuration, when its save fails, or after a read.
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

        public T Read<T>(Func<PluginConfiguration, T> read)
        {
            var result = inner.Read(read);
            var after = AfterRead;
            AfterRead = null;
            after?.Invoke();
            return result;
        }

        public void Update(Action<PluginConfiguration> update)
        {
            var before = BeforeUpdate;
            BeforeUpdate = null;
            before?.Invoke();
            try
            {
                inner.Update(update);
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
                case "/Users/AuthenticateByName":
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
                case "/Users/AuthenticateByName":
                    string token;
                    bool first;
                    lock (_gate)
                    {
                        _logins++;
                        token = TestValues.Token + _logins.ToString(CultureInfo.InvariantCulture);
                        _live = token;
                        first = holdFirstLogin && _logins == 1;
                    }

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
