using System;
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

    [Fact]
    public async Task WithoutALiveLoginNothingIsSentAndTheDeviceIsSaidToStay()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        var logger = new CapturingLogger<RemoteServerRegistry>();
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, fixture.Client, logger);
        fixture.Handler.Requests.Clear();

        Assert.True(await restarted.RemoveAsync(server.Id));

        Assert.Empty(fixture.Handler.Requests);
        Assert.Contains(logger.Lines, line => line.Contains("no live login", StringComparison.Ordinal) && line.Contains("Friend", StringComparison.Ordinal));
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
        using var restarted = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, new CapturingLogger<RemoteServerRegistry>());

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
        var logout = Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == LogoutPath);
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
        Assert.Single(fixture.Handler.Requests, request => request.Uri.AbsolutePath == LogoutPath);
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

    // Runs a step once, just before or just after the next change of the configuration, or when its save fails.
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

        public T Read<T>(Func<PluginConfiguration, T> read) => inner.Read(read);

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
}
