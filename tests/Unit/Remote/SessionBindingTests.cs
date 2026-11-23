using System;
using System.Collections.Generic;
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

// A login goes only to the address it was made at, with the credentials it was made with; a restored row can change both.
public sealed class SessionBindingTests : IDisposable
{
    private const string LoginPath = "/Users/AuthenticateByName";
    private const string LogoutPath = "/Sessions/Logout";
    private const string ViewsPath = "/UserViews";
    private const string OtherHost = "other.invalid";
    private const string OtherUrl = "http://other.invalid:8096";
    private const string RemoteId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";

    private static readonly RemoteCredentials OtherCredentials = new() { Username = "other-user", Password = "other-pass" };

    private readonly RegistryFixture _fixture;
    private readonly SemaphoreSlim _loginHeld = new(0);
    private ManualResetEventSlim? _holdLogin;
    private int _logins;

    public SessionBindingTests()
    {
        _fixture = new RegistryFixture(new StubHandler(Respond));
    }

    public void Dispose()
    {
        _fixture.Dispose();
        _loginHeld.Dispose();
    }

    private RemoteServerConfig Row => _fixture.Store.Configuration.Servers.Single();

    private List<StubHandler.RecordedRequest> Requests => _fixture.Handler.Requests;

    // Each login gets a token of its own; a held login is refused once it is let go.
    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case LoginPath when _holdLogin is { } hold:
                _holdLogin = null;
                _loginHeld.Release();
                hold.Wait(TimeSpan.FromSeconds(10));
                return StubHandler.Status(HttpStatusCode.Unauthorized);
            case LoginPath:
                return StubHandler.Json("{\"ServerId\":\"" + RemoteId + "\"," + TestValues.LoginJson[1..].Replace(TestValues.Token, "token-" + ++_logins, StringComparison.Ordinal));
            case "/System/Info/Public":
                return StubHandler.Json("{\"ServerName\":\"Friend box\",\"Id\":\"" + RemoteId + "\"}");
            case ViewsPath:
                return StubHandler.Json(TestValues.ViewsJson);
            default:
                return StubHandler.Status(request.RequestUri.AbsolutePath == LogoutPath ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        }
    }

    private async Task<Guid> AddAsync()
    {
        var server = await _fixture.AddServerAsync();
        Requests.Clear();
        return server.Id;
    }

    private Task<RemoteResult<IReadOnlyList<RemoteLibrary>>> ReadAsync(Guid id)
        => _fixture.Registry.GetLibrariesAsync(id, CancellationToken.None);

    private Task<RemoteResult<RemoteEditReport>> EditAsync(Guid id, string url, string user = TestValues.Username, string? password = null)
        => _fixture.Registry.SaveEditAsync(id, new RemoteServerEdit { Name = "Friend", Url = url, Username = user, Password = password }, null, CancellationToken.None);

    private static bool Carries(StubHandler.RecordedRequest request, string token)
        => request.Authorization?.Contains("\"" + token + "\"", StringComparison.Ordinal) == true;

    private void AssertLogClean()
    {
        foreach (var secret in new[] { "token-", TestValues.Host, OtherHost, TestValues.Password, OtherCredentials.Password })
        {
            Assert.DoesNotContain(_fixture.Logger.Lines, line => line.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task AStoredAddressThatChangedGetsALoginOfItsOwn()
    {
        var id = await AddAsync();
        Row.Url = OtherUrl;

        Assert.True((await ReadAsync(id)).IsSuccess);

        Assert.DoesNotContain(Requests, request => Carries(request, "token-1"));
        Assert.All(Requests, request => Assert.Equal(OtherHost, request.Uri.Host));
        Assert.Equal([LoginPath, ViewsPath], Requests.Select(request => request.Uri.AbsolutePath));
        Assert.True(Carries(Requests[^1], "token-2"));
        AssertLogClean();
    }

    [Fact]
    public async Task StoredCredentialsThatChangedGetALoginOfTheirOwn()
    {
        var id = await AddAsync();
        Row.EncryptedCredentials = _fixture.Vault.Protect(id, OtherCredentials)!;

        Assert.True((await ReadAsync(id)).IsSuccess);

        Assert.DoesNotContain(Requests, request => Carries(request, "token-1"));
        Assert.Contains(OtherCredentials.Username, Requests[0].Body, StringComparison.Ordinal);
        Assert.True(Carries(Requests[^1], "token-2"));
        AssertLogClean();
    }

    [Fact]
    public async Task CredentialsThatNoLongerDecryptEndTheUseOfTheLogin()
    {
        var id = await AddAsync();
        Row.EncryptedCredentials = "damaged";

        Assert.False((await ReadAsync(id)).IsSuccess);

        Assert.Empty(Requests);
    }

    [Fact]
    public async Task AnOlderRowBroughtBackAfterAnEditGetsALoginOfItsOwn()
    {
        var id = await AddAsync();
        var (url, encrypted) = (Row.Url, Row.EncryptedCredentials);
        Assert.True((await EditAsync(id, OtherUrl, OtherCredentials.Username, OtherCredentials.Password)).IsSuccess);
        Requests.Clear();
        (Row.Url, Row.EncryptedCredentials) = (url, encrypted);

        Assert.True((await ReadAsync(id)).IsSuccess);

        Assert.All(Requests, request => Assert.Equal(TestValues.Host, request.Uri.Host));
        Assert.DoesNotContain(Requests, request => Carries(request, "token-1") || Carries(request, "token-2"));
        Assert.Contains(TestValues.Username, Requests[0].Body, StringComparison.Ordinal);
        AssertLogClean();
    }

    [Fact]
    public async Task ACallThatWaitedForALoginUsesTheAddressStoredWhenItsTurnComes()
    {
        var id = await AddAsync();
        using var restarted = new RemoteServerRegistry(_fixture.Store, _fixture.Vault, _fixture.Client, _fixture.Logger);
        using var hold = new ManualResetEventSlim(false);
        _holdLogin = hold;
        var first = Task.Run(() => restarted.GetLibrariesAsync(id, CancellationToken.None));
        Assert.True(await _loginHeld.WaitAsync(TimeSpan.FromSeconds(10)));
        var second = restarted.GetLibrariesAsync(id, CancellationToken.None);
        Assert.False(second.IsCompleted);

        (Row.Url, Row.EncryptedCredentials) = (OtherUrl, _fixture.Vault.Protect(id, OtherCredentials)!);
        hold.Set();

        Assert.False((await first).IsSuccess);
        Assert.True((await second).IsSuccess);
        Assert.Equal(TestValues.Host, Requests[0].Uri.Host);
        Assert.All(Requests.Skip(1), request => Assert.Equal(OtherHost, request.Uri.Host));
        Assert.Contains(OtherCredentials.Username, Requests[1].Body, StringComparison.Ordinal);
        AssertLogClean();
    }

    [Fact]
    public async Task ALoginThatEndsAfterTheRowChangedIsNotUsedAgain()
    {
        var id = await AddAsync();
        using var restarted = new RemoteServerRegistry(_fixture.Store, _fixture.Vault, _fixture.Client, _fixture.Logger);
        var first = await restarted.RunWithSessionAsync(
            id,
            (endpoint, session, _) =>
            {
                Row.Url = OtherUrl;
                return Task.FromResult(RemoteResult.Success((endpoint.BaseUrl.Host, session.AccessToken)));
            },
            CancellationToken.None);

        var second = await restarted.RunWithSessionAsync(id, (endpoint, session, _) => Task.FromResult(RemoteResult.Success((endpoint.BaseUrl.Host, session.AccessToken))), CancellationToken.None);

        Assert.Equal((TestValues.Host, "token-2"), first.Value);
        Assert.Equal((OtherHost, "token-3"), second.Value);
    }

    [Fact]
    public async Task ARefusedTokenLogsInAgainAtTheAddressStoredThen()
    {
        var id = await AddAsync();
        var seen = new List<(string Host, string Token)>();

        var result = await _fixture.Registry.RunWithSessionAsync(
            id,
            (endpoint, session, _) =>
            {
                seen.Add((endpoint.BaseUrl.Host, session.AccessToken));
                Row.Url = OtherUrl;
                return Task.FromResult(seen.Count == 1 ? RemoteResult.Fail<int>(RemoteFailure.AuthenticationFailed) : RemoteResult.Success(1));
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([(TestValues.Host, "token-1"), (OtherHost, "token-2")], seen);
        Assert.All(Requests, request => Assert.Equal(OtherHost, request.Uri.Host));
    }

    [Fact]
    public async Task TheRemovalOfAServerWhoseRowChangedEndsTheLoginWhereItWasMade()
    {
        var id = await AddAsync();
        Row.Url = OtherUrl;

        Assert.True(await _fixture.Registry.RemoveAsync(id));

        var logout = Assert.Single(Requests, request => Carries(request, "token-1"));
        Assert.Equal((TestValues.Host, LogoutPath), (logout.Uri.Host, logout.Uri.AbsolutePath));
        AssertLogClean();
    }

    [Fact]
    public async Task ANewUserSavedOnARowThatChangedEndsTheOldLoginWhereItWasMade()
    {
        var id = await AddAsync();
        Row.Url = OtherUrl;

        Assert.True((await EditAsync(id, OtherUrl, OtherCredentials.Username, OtherCredentials.Password)).IsSuccess);

        var logout = Assert.Single(Requests, request => Carries(request, "token-1"));
        Assert.Equal((TestValues.Host, LogoutPath), (logout.Uri.Host, logout.Uri.AbsolutePath));
        AssertLogClean();
    }

    // Row 434: the live login stays when the edit finds the saved server at its new address.
    [Fact]
    public async Task ANewAddressSavedWithTheSameUserKeepsTheLoginOfTheSavedServer()
    {
        var id = await AddAsync();

        Assert.True((await EditAsync(id, OtherUrl)).IsSuccess);
        Requests.Clear();
        Assert.True((await ReadAsync(id)).IsSuccess);

        var read = Assert.Single(Requests);
        Assert.Equal((OtherHost, ViewsPath), (read.Uri.Host, read.Uri.AbsolutePath));
        Assert.True(Carries(read, "token-1"));
    }

    [Fact]
    public async Task ANewAddressSavedWithNoServerIdToCheckGetsALoginOfItsOwn()
    {
        var id = await AddAsync();
        Row.JellyfinServerId = string.Empty;

        Assert.True((await EditAsync(id, OtherUrl)).IsSuccess);
        Assert.True((await ReadAsync(id)).IsSuccess);

        Assert.DoesNotContain(Requests, request => Carries(request, "token-1"));
    }

    [Fact]
    public async Task ALoginThatFollowedAnEditIsDroppedWhenTheOlderRowComesBack()
    {
        var id = await AddAsync();
        Assert.True((await EditAsync(id, OtherUrl)).IsSuccess);
        Requests.Clear();
        Row.Url = TestValues.Url;

        Assert.True((await ReadAsync(id)).IsSuccess);

        Assert.DoesNotContain(Requests, request => Carries(request, "token-1"));
        Assert.Equal([LoginPath, ViewsPath], Requests.Select(request => request.Uri.AbsolutePath));
    }

    // A login on a row that changed while it ran does not follow an edit made on the older row.
    [Fact]
    public async Task ALoginOfAnotherRowDoesNotFollowAnEdit()
    {
        var id = await AddAsync();
        Row.Url = "http://third.invalid:8096";

        Assert.True((await EditAsync(id, OtherUrl)).IsSuccess);
        Assert.True((await ReadAsync(id)).IsSuccess);

        Assert.DoesNotContain(Requests, request => Carries(request, "token-1"));
    }
}
