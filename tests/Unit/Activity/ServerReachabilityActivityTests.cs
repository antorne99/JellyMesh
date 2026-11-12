using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Activity;

public class ServerReachabilityActivityTests
{
    private const string Offline = "JellyMeshServerOffline";
    private const string Reachable = "JellyMeshServerReachable";

    [Fact]
    public async Task Going_offline_and_coming_back_writes_one_entry_each()
    {
        var remote = new SwitchableRemote();
        using var registry = remote.NewRegistry();
        var server = await remote.AddAsync(registry);

        remote.Answer = Answer.Offline;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        remote.Answer = Answer.Stock;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);

        Assert.Collection(
            remote.Activity.Entries,
            offline =>
            {
                Assert.Equal(Offline, offline.Type);
                Assert.Equal(LogLevel.Warning, offline.Severity);
                Assert.Contains("Friend", offline.Name, StringComparison.Ordinal);
            },
            back =>
            {
                Assert.Equal(Reachable, back.Type);
                Assert.Equal(LogLevel.Information, back.Severity);
                Assert.Contains("Friend", back.Name, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task After_a_restart_the_first_failure_is_written_and_the_first_answer_is_not()
    {
        var remote = new SwitchableRemote();
        using (var before = remote.NewRegistry())
        {
            await remote.AddAsync(before);
        }

        var serverId = remote.Store.Configuration.Servers.Single().Id;
        using var answering = remote.NewRegistry();
        await answering.GetLibrariesAsync(serverId, CancellationToken.None);
        Assert.Empty(remote.Activity.Entries);

        remote.Answer = Answer.Offline;
        using var failing = remote.NewRegistry();
        await failing.GetLibrariesAsync(serverId, CancellationToken.None);
        Assert.Equal(Offline, Assert.Single(remote.Activity.Entries).Type);
    }

    [Fact]
    public async Task A_refused_login_after_an_outage_means_the_server_answers_again()
    {
        var remote = new SwitchableRemote();
        using var registry = remote.NewRegistry();
        var server = await remote.AddAsync(registry);

        remote.Answer = Answer.Offline;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        remote.Answer = Answer.Unauthorized;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);

        Assert.Equal([Offline, Reachable], remote.Activity.Entries.Select(entry => entry.Type));
    }

    [Fact]
    public async Task A_gateway_error_says_nothing_about_the_server_behind_it()
    {
        var remote = new SwitchableRemote();
        using var registry = remote.NewRegistry();
        var server = await remote.AddAsync(registry);

        remote.Answer = Answer.Offline;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        remote.Answer = Answer.BadGateway;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);

        Assert.Equal([Offline], remote.Activity.Entries.Select(entry => entry.Type));
    }

    [Fact]
    public async Task A_suspended_call_sends_nothing_and_writes_nothing()
    {
        var remote = new SwitchableRemote();
        using var registry = remote.NewRegistry();
        var server = await remote.AddAsync(registry);

        remote.KeySource.Key = TestValues.OtherKey;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);

        Assert.Empty(remote.Activity.Entries);
    }

    [Fact]
    public async Task No_entry_holds_the_address_or_the_token()
    {
        var remote = new SwitchableRemote();
        using var registry = remote.NewRegistry();
        var server = await remote.AddAsync(registry);

        remote.Answer = Answer.Offline;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);
        remote.Answer = Answer.Stock;
        await registry.GetLibrariesAsync(server.Id, CancellationToken.None);

        Assert.Equal(2, remote.Activity.Entries.Count);
        foreach (var entry in remote.Activity.Entries)
        {
            var text = string.Join(' ', entry.Name, entry.ShortOverview, entry.Overview);
            Assert.DoesNotContain(TestValues.Host, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(TestValues.Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Password, text, StringComparison.Ordinal);
        }
    }

    private enum Answer
    {
        Stock,
        Offline,
        Unauthorized,
        BadGateway
    }

    // One remote whose answers can be switched, shared by registries that stand for restarts of this server.
    private sealed class SwitchableRemote
    {
        public SwitchableRemote()
        {
            KeySource = new FixedKeySource(TestValues.Key);
            Handler = new StubHandler(request => Answer switch
            {
                Answer.Offline => throw new HttpRequestException("failed for http://" + TestValues.Host + ":8096/?api_key=" + TestValues.Token),
                Answer.Unauthorized => StubHandler.Status(HttpStatusCode.Unauthorized),
                Answer.BadGateway => StubHandler.Status(HttpStatusCode.BadGateway),
                _ => StubHandler.StockServer_Respond(request)
            });
        }

        public Answer Answer { get; set; }

        public StubHandler Handler { get; }

        public InMemoryConfigurationStore Store { get; } = new();

        public FixedKeySource KeySource { get; }

        public RecordingActivityWriter Activity { get; } = new();

        public RemoteServerRegistry NewRegistry()
        {
            var vault = new CredentialVault(KeySource, Store);
            var client = new JellyfinRemoteClient(new StubHttpClientFactory(Handler)) { Delay = (_, _) => Task.CompletedTask };
            return new RemoteServerRegistry(Store, vault, client, new CapturingLogger<RemoteServerRegistry>(), Activity);
        }

        public async Task<RemoteServerInfo> AddAsync(RemoteServerRegistry registry)
        {
            var result = await registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure.ToString());
            return result.Value!;
        }
    }
}
