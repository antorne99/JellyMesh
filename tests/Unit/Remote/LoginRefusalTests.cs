using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Remote;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Remote;

// Rows 419 and 433: a login the remote opened and that is refused is ended, and a saved Server Id is the only one accepted.
public class LoginRefusalTests
{
    private const string FirstId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";
    private const string RefusedToken = "refused-tok3n";
    private const string AdminLogin = "{\"ServerId\":\"" + FirstId + "\",\"AccessToken\":\"" + RefusedToken + "\",\"User\":{\"Id\":\"u\",\"Policy\":{\"IsAdministrator\":true}}}";
    private const string NoPolicyLogin = "{\"ServerId\":\"" + FirstId + "\",\"AccessToken\":\"" + RefusedToken + "\",\"User\":{\"Id\":\"u\"}}";

    private string _login = "{\"ServerId\":\"" + FirstId + "\"," + TestValues.LoginJson[1..];

    private StubHandler Remote() => new(request => request.RequestUri!.AbsolutePath == "/Users/AuthenticateByName"
        ? StubHandler.Json(_login)
        : StubHandler.StockServer_Respond(request));

    private static Task<RemoteResult<int>> PingAsync(RemoteServerRegistry registry, Guid id)
        => registry.RunWithSessionAsync(id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);

    // A registry over the same store, as after a restart, so that the next call logs in again.
    private static RemoteServerRegistry Restarted(RegistryFixture fixture) => new(fixture.Store, fixture.Vault, fixture.Client, fixture.Logger);

    private static bool EndsRefusedLogin(RegistryFixture fixture)
        => fixture.Handler.Requests.Any(request => request.Uri.AbsolutePath == "/Sessions/Logout" && request.Authorization!.Contains(RefusedToken, StringComparison.Ordinal));

    [Theory]
    [InlineData(AdminLogin)]
    [InlineData(NoPolicyLogin)]
    public async Task ARefusedLoginOfAnAddIsEnded(string answer)
    {
        using var fixture = new RegistryFixture(Remote());
        _login = answer;

        var result = await fixture.Registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.True(EndsRefusedLogin(fixture));
        Assert.Empty(fixture.Store.Configuration.Servers);
    }

    [Theory]
    [InlineData(AdminLogin)]
    [InlineData(NoPolicyLogin)]
    public async Task ARefusedLoginOfACallIsEnded(string answer)
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        _login = answer;
        using var restarted = Restarted(fixture);

        var result = await PingAsync(restarted, server.Id);

        Assert.False(result.IsSuccess);
        Assert.True(EndsRefusedLogin(fixture));
    }

    [Fact]
    public async Task ARefusedLoginOfNewCredentialsIsEnded()
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        _login = AdminLogin;

        var result = await fixture.Registry.EnterCredentialsAsync(server, CredentialsEntry.NewPassword, CancellationToken.None);

        Assert.Equal(RemoteFailure.AdministratorAccount, result.Failure);
        Assert.True(EndsRefusedLogin(fixture));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"ServerId\":\"not-an-id\",")]
    [InlineData("\"ServerId\":\"00000000000000000000000000000000\",")]
    public async Task ALoginWithNoUsableServerIdIsRefusedOnceOneIsSaved(string serverId)
    {
        using var fixture = new RegistryFixture(Remote());
        var server = await fixture.AddServerAsync();
        _login = "{" + serverId + TestValues.LoginJson[1..];
        fixture.Handler.Requests.Clear();
        using var restarted = Restarted(fixture);

        var call = await PingAsync(restarted, server.Id);
        var credentials = await fixture.Registry.EnterCredentialsAsync(server, CredentialsEntry.NewPassword, CancellationToken.None);

        Assert.Equal(RemoteFailure.OtherServer, call.Failure);
        Assert.Equal(RemoteFailure.OtherServer, credentials.Failure);
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath == "/Sessions/Logout"));
        Assert.Equal(FirstId, fixture.Store.Configuration.Servers.Single().JellyfinServerId);
    }

    [Fact]
    public async Task ALoginWithNoServerIdIsAcceptedWhileNoneIsSaved()
    {
        using var fixture = new RegistryFixture(Remote());
        _login = TestValues.LoginJson;
        var server = await fixture.AddServerAsync();
        using var restarted = Restarted(fixture);

        var call = await PingAsync(restarted, server.Id);

        Assert.True(call.IsSuccess, call.Failure.ToString());
    }
}
