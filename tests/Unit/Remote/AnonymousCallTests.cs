using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

// Row 534: anyone can make an anonymous call, so it leaves the state of the server alone and cannot fill the log.
public class AnonymousCallTests
{
    private const string FirstId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";
    private const string OtherId = "1e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";
    private const string AdminLogin = "{\"ServerId\":\"" + FirstId + "\",\"AccessToken\":\"t\",\"User\":{\"Id\":\"u\",\"Policy\":{\"IsAdministrator\":true}}}";

    private readonly ManualClock _clock = new();
    private string _login = "{\"ServerId\":\"" + FirstId + "\"," + TestValues.LoginJson[1..];

    private StubHandler Remote() => new(request => request.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"
        ? StubHandler.Json(_login)
        : StubHandler.StockServer_Respond(request));

    // A registry over the same store with the clock of the test, as after a restart, so that the next call logs in again.
    private RemoteServerRegistry Restarted(RegistryFixture fixture)
        => new(fixture.Store, fixture.Vault, fixture.Client, fixture.Logger) { Clock = _clock };

    private static Task<RemoteResult<int>> AnonymousAsync(RemoteServerRegistry registry, Guid id, RemoteFailure failure)
        => registry.RunAnonymousCallAsync(id, (_, _, _) => Task.FromResult(AnswerOf(failure)), CancellationToken.None);

    private static Task<RemoteResult<int>> PlaybackAsync(RemoteServerRegistry registry, Guid id, RemoteFailure failure)
        => registry.RunPlaybackCallAsync(id, (_, _, _) => Task.FromResult(AnswerOf(failure)), CancellationToken.None);

    private static RemoteResult<int> AnswerOf(RemoteFailure failure)
        => failure == RemoteFailure.None ? RemoteResult.Success(1) : RemoteResult.Fail<int>(failure);

    private static (RemoteFailure? Failure, DateTimeOffset? Checked) StateOf(RemoteServerRegistry registry)
    {
        var server = registry.GetServers().Single();
        return (server.LastFailure, server.LastCheckedUtc);
    }

    // A second server, saved as if added with a Server Id.
    private static Guid AddSecond(RegistryFixture fixture, string remoteId)
    {
        var id = Guid.NewGuid();
        fixture.Store.Configuration.Servers.Add(new()
        {
            Id = id,
            Name = "Second",
            Url = TestValues.Url,
            EncryptedCredentials = fixture.Vault.Protect(id, TestValues.Credentials)!,
            JellyfinServerId = remoteId
        });
        return id;
    }

    [Theory]
    [InlineData(RemoteFailure.None, RemoteFailure.Unreachable)]
    [InlineData(RemoteFailure.None, RemoteFailure.AuthenticationFailed)]
    [InlineData(RemoteFailure.None, RemoteFailure.None)]
    [InlineData(RemoteFailure.Unreachable, RemoteFailure.None)]
    [InlineData(RemoteFailure.Timeout, RemoteFailure.ServerError)]
    public async Task AnAnonymousCallLeavesTheStateOfTheServerAlone(RemoteFailure before, RemoteFailure anonymous)
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        await PlaybackAsync(fixture.Registry, server.Id, before);
        var state = StateOf(fixture.Registry);

        var result = await AnonymousAsync(fixture.Registry, server.Id, anonymous);

        Assert.Equal(anonymous, result.Failure);
        Assert.Equal((before, state.Checked), state);
        Assert.Equal(state, StateOf(fixture.Registry));
    }

    [Fact]
    public async Task BeforeAnyOtherCallAnAnonymousOneLeavesNoState()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        using var restarted = Restarted(fixture);

        await AnonymousAsync(restarted, server.Id, RemoteFailure.Unreachable);

        Assert.Equal((null, null), StateOf(restarted));
    }

    [Fact]
    public async Task AFailingServerIsLoggedOncePerInterval()
    {
        using var fixture = new RegistryFixture();
        var friend = await fixture.AddServerAsync();
        using var registry = Restarted(fixture);
        fixture.Logger.Lines.Clear();

        foreach (var failure in new[] { RemoteFailure.Unreachable, RemoteFailure.Timeout, RemoteFailure.None, RemoteFailure.ServerError, RemoteFailure.AuthenticationFailed })
        {
            await AnonymousAsync(registry, friend.Id, failure);
        }

        _clock.Advance(RemoteServerRegistry.AnonymousLogInterval - TimeSpan.FromSeconds(1));
        await AnonymousAsync(registry, friend.Id, RemoteFailure.Timeout);
        var line = Assert.Single(fixture.Logger.Lines);
        Assert.Contains("Friend", line, StringComparison.Ordinal);
        Assert.Contains(nameof(RemoteFailure.Unreachable), line, StringComparison.Ordinal);
        Assert.DoesNotContain(new Uri(TestValues.Url).Host, line, StringComparison.Ordinal);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await AnonymousAsync(registry, friend.Id, RemoteFailure.Timeout);
        Assert.Equal(2, fixture.Logger.Lines.Count);
        Assert.Contains(nameof(RemoteFailure.Timeout), fixture.Logger.Lines[1], StringComparison.Ordinal);
    }

    // The interval is of each server, and the calls of signed-in users keep their own lines.
    [Fact]
    public async Task OtherServersAndOtherCallsKeepTheirLines()
    {
        using var fixture = new RegistryFixture();
        var friend = await fixture.AddServerAsync();
        var second = AddSecond(fixture, string.Empty);
        using var registry = Restarted(fixture);
        fixture.Logger.Lines.Clear();

        foreach (var id in new[] { friend.Id, second, friend.Id, second })
        {
            await AnonymousAsync(registry, id, RemoteFailure.Unreachable);
        }

        await PlaybackAsync(registry, friend.Id, RemoteFailure.Unreachable);
        await PlaybackAsync(registry, friend.Id, RemoteFailure.Unreachable);

        Assert.Equal(3, fixture.Logger.Lines.Count(line => line.Contains("Friend", StringComparison.Ordinal)));
        Assert.Single(fixture.Logger.Lines, line => line.Contains("Second", StringComparison.Ordinal));
    }

    // A login the remote opens and the plugin refuses is ended, and told by the same single line.
    [Theory]
    [InlineData(AdminLogin, RemoteFailure.AdministratorAccount)]
    [InlineData("{\"ServerId\":\"" + OtherId + "\"," + "\"AccessToken\":\"t\",\"User\":{\"Id\":\"u\",\"Policy\":{\"IsAdministrator\":false}}}", RemoteFailure.OtherServer)]
    public async Task ARefusedLoginIsLoggedOncePerInterval(string login, RemoteFailure expected)
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        using var registry = Restarted(fixture);
        fixture.Logger.Lines.Clear();
        _login = login;

        for (var turn = 0; turn < 3; turn++)
        {
            Assert.Equal(expected, (await AnonymousAsync(registry, server.Id, RemoteFailure.None)).Failure);
        }

        Assert.Contains(expected.ToString(), Assert.Single(fixture.Logger.Lines), StringComparison.Ordinal);
        Assert.Equal(3, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/Sessions/Logout"));
    }

    // Decision 128: a Server Id another server holds is not stored and is told, also within the line of the interval.
    [Fact]
    public async Task AServerIdHeldElsewhereIsToldOncePerInterval()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        AddSecond(fixture, FirstId);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(Remote()));
        using var registry = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, fixture.Logger) { Clock = _clock };
        fixture.Logger.Lines.Clear();

        Assert.Equal(RemoteFailure.None, (await AnonymousAsync(registry, server.Id, RemoteFailure.None)).Failure);

        await AnonymousAsync(registry, server.Id, RemoteFailure.Timeout);
        Assert.Contains("which another configured server holds", Assert.Single(fixture.Logger.Lines), StringComparison.Ordinal);
        Assert.Empty(fixture.Store.Configuration.Servers[0].JellyfinServerId);

        using var silent = new RemoteServerRegistry(fixture.Store, fixture.Vault, client, fixture.Logger) { Clock = _clock };
        _login = AdminLogin;
        await AnonymousAsync(silent, server.Id, RemoteFailure.None);
        _login = "{\"ServerId\":\"" + FirstId + "\"," + TestValues.LoginJson[1..];
        await AnonymousAsync(silent, server.Id, RemoteFailure.None);
        Assert.Equal(2, fixture.Logger.Lines.Count);
        Assert.Contains(nameof(RemoteFailure.AdministratorAccount), fixture.Logger.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClockSetBackDoesNotSilenceTheLog()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        using var registry = Restarted(fixture);
        fixture.Logger.Lines.Clear();

        await AnonymousAsync(registry, server.Id, RemoteFailure.Timeout);
        _clock.Advance(TimeSpan.FromDays(-1));
        await AnonymousAsync(registry, server.Id, RemoteFailure.Timeout);

        Assert.Equal(2, fixture.Logger.Lines.Count);
    }

    [Fact]
    public async Task ManyCallsAtOnceWriteOneLine()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        using var registry = Restarted(fixture);
        await AnonymousAsync(registry, server.Id, RemoteFailure.None);
        fixture.Logger.Lines.Clear();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task> calls = [];
        for (var call = 0; call < 32; call++)
        {
            calls.Add(registry.RunAnonymousCallAsync(
                server.Id,
                async (_, _, _) =>
                {
                    await start.Task;
                    return RemoteResult.Fail<int>(RemoteFailure.Unreachable);
                },
                CancellationToken.None));
        }

        start.SetResult();
        await Task.WhenAll(calls);

        Assert.Single(fixture.Logger.Lines);
    }

    [Fact]
    public async Task AnUnusableKeySendsNothing()
    {
        using var fixture = new RegistryFixture();
        var server = await fixture.AddServerAsync();
        fixture.Handler.Requests.Clear();
        fixture.KeySource.Key = null;

        Assert.Equal(RemoteFailure.Suspended, (await AnonymousAsync(fixture.Registry, server.Id, RemoteFailure.None)).Failure);

        Assert.Empty(fixture.Handler.Requests);
    }
}
