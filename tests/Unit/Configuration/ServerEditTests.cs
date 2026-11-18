using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Sync;
using Jellyfin.Plugin.JellyMesh.Tests.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Configuration;

// Rows 432-434: the edit form of a server, its check and its save.
public sealed partial class ServerEditTests : IDisposable
{
    private const string FirstId = "8e3f1c2a4b5d4e6f8a9b0c1d2e3f4a5b";
    private const string OtherId = "1a2b3c4d5e6f40718293a4b5c6d7e8f9";
    private const string OtherUrl = "http://other.invalid:8096";

    private readonly RegistryFixture _fixture;
    private readonly CoordinatorFixture _playback = new();
    private readonly Mock<IServerLibraryManager> _libraries = new();
    private string _serverId = FirstId;
    private string _views = TestValues.ViewsJson;
    private HttpStatusCode _login = HttpStatusCode.OK;
    private bool _admin;
    private bool _publicSet;
    private string? _publicOverride;
    private string _token = TestValues.Token;
    private HttpStatusCode _logout = HttpStatusCode.NoContent;
    private bool _offline;

    private string? PublicId => _publicSet ? _publicOverride : _serverId;

    private string PublicInfo => "{\"ServerName\":\"" + (PublicId == FirstId ? "Friend box" : "Stranger box") + "\""
        + (PublicId is null ? string.Empty : ",\"Id\":\"" + PublicId + "\"") + "}";

    private string LoginAnswer => "{\"ServerId\":\"" + _serverId + "\"," + (_admin ? TestValues.LoginJson[1..].Replace("false", "true", StringComparison.Ordinal) : TestValues.LoginJson[1..])
        .Replace(TestValues.Token, _token, StringComparison.Ordinal);

    public ServerEditTests()
    {
        _fixture = new RegistryFixture(new StubHandler(Respond));
        _libraries.Setup(manager => manager.GetState(It.IsAny<Guid>()))
            .Returns(new AutomaticLibrariesState(true, false, [new AutomaticLibraryStatus(ServerLibraryKind.Movies, "Friend - Film")], [], AutomaticLibrariesError.None));
    }

    public void Dispose() => _fixture.Dispose();

    private StubHandler.RecordedRequest[] Requests => _fixture.Handler.Requests.ToArray();

    private System.Net.Http.HttpResponseMessage Respond(System.Net.Http.HttpRequestMessage request) => _offline ? throw new System.Net.Http.HttpRequestException("offline") : request.RequestUri!.AbsolutePath switch
    {
        "/Users/AuthenticateByName" => _login == HttpStatusCode.OK ? StubHandler.Json(LoginAnswer) : StubHandler.Status(_login),
        "/UserViews" => StubHandler.Json(_views),
        "/System/Info/Public" => StubHandler.Json(PublicInfo),
        "/Sessions/Logout" => StubHandler.Status(_logout),
        _ => StubHandler.Status(HttpStatusCode.NotFound)
    };

    private FederationAdminController Controller(IServerRenamer? renamer = null) => new(
        _fixture.Registry,
        _fixture.Vault,
        _fixture.Store,
        Mock.Of<IFederatedFolderLinkChecker>(),
        _playback.Coordinator,
        new FixedFederatedRootSource(null),
        Mock.Of<ICatalogSyncService>(),
        new InMemoryMergeStore(),
        _libraries.Object,
        new CapturingLogger<FederationAdminController>(),
        renamer: renamer);

    private async Task<Guid> AddAsync(params string[] libraries)
    {
        var server = await _fixture.AddServerAsync();
        Assert.True(_fixture.Registry.SetImportedLibraries(server.Id, libraries));
        _fixture.Handler.Requests.Clear();
        return server.Id;
    }

    private static ServerEditRequest Edit(string name = "Friend", string url = TestValues.Url, string user = TestValues.Username, string? password = null, IReadOnlyList<string>? libraries = null)
        => new() { Name = name, Url = url, Username = user, Password = password, LibraryIds = libraries };

    private static string DeviceOf(StubHandler.RecordedRequest request)
        => request.Authorization!.Split("DeviceId=\"")[1].Split('"')[0];

    private static string ToJson(object? value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), JsonDefaults.Options);

    private static (int Status, FailureResponse Failure) Refusal<T>(ActionResult<T> result)
    {
        var refused = Assert.IsType<ObjectResult>(result.Result);
        return (refused.StatusCode!.Value, Assert.IsType<FailureResponse>(refused.Value));
    }

    private string Snapshot(Guid? id = null)
    {
        var server = _fixture.Store.Configuration.Servers.Single(candidate => id is null || candidate.Id == id);
        return string.Join('|', server.Name, server.Url, server.EncryptedCredentials, server.JellyfinServerId, string.Join(',', server.ImportedLibraryIds));
    }

    [Fact]
    public async Task A_check_saves_nothing_and_lists_the_libraries_the_user_sees()
    {
        var id = await AddAsync("1");
        var before = Snapshot();
        var saves = _fixture.Store.Saves;

        var result = await Controller().CheckServerEdit(id, Edit(name: "Renamed", url: OtherUrl, user: "someone", password: "new-pass", libraries: ["2"]), CancellationToken.None);

        var check = Assert.IsType<ServerEditCheck>(result.Value);
        Assert.Equal(saves, _fixture.Store.Saves);
        Assert.Equal(before, Snapshot());
        Assert.Equal("Friend box", check.ServerName);
        Assert.Equal(["1", "2"], check.Libraries.Select(library => library.Id));
        Assert.Equal(["2"], check.Libraries.Where(library => library.Selected).Select(library => library.Id));
    }

    [Fact]
    public async Task A_check_logs_in_on_a_device_of_its_own_and_ends_that_login()
    {
        var id = await AddAsync("1");
        await _fixture.Registry.RunWithSessionAsync(id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);
        _fixture.Handler.Requests.Clear();

        await Controller().CheckServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        var login = Assert.Single(Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
        var logout = Assert.Single(Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
        Assert.Equal($"jellymesh-{id:N}-check", DeviceOf(login));
        Assert.Equal(DeviceOf(login), DeviceOf(logout));
        Assert.Equal("other.invalid", login.Uri.Host);
        Assert.Contains(TestValues.Password, login.Body, StringComparison.Ordinal);

        // The kept login was not touched, so a call goes on with it.
        _fixture.Handler.Requests.Clear();
        await _fixture.Registry.RunWithSessionAsync(id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);
        Assert.DoesNotContain(Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task A_check_refuses_another_server_at_a_new_address_before_sending_the_password()
    {
        var id = await AddAsync("1");
        _serverId = OtherId;

        var result = await Controller().CheckServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        var (status, failure) = Refusal(result);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal(RemoteFailure.OtherServer, failure.Failure);
        Assert.Equal(RemoteEditField.Url, failure.Field);
        Assert.Equal("Stranger box", failure.ServerName);
        Assert.Equal("Another server answers at this address. To use it, remove this one and add it as a new server.", failure.Message);
        var request = Assert.Single(Requests);
        Assert.Equal("/System/Info/Public", request.Uri.AbsolutePath);
        Assert.Null(request.Body);
        Assert.DoesNotContain("Token=", request.Authorization ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_address_with_no_usable_id_is_refused_before_sending_the_password()
    {
        var id = await AddAsync("1");
        _publicSet = true;
        _publicOverride = null;

        var result = await Controller().CheckServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        Assert.Equal(RemoteFailure.OtherServer, Refusal(result).Failure.Failure);
        Assert.DoesNotContain(Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task Another_server_seen_only_at_login_is_still_refused_and_its_login_ended()
    {
        var id = await AddAsync("1");
        _publicSet = true;
        _publicOverride = FirstId;
        _serverId = OtherId;

        var result = await Controller().CheckServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        Assert.Equal(RemoteFailure.OtherServer, Refusal(result).Failure.Failure);
        Assert.Contains(Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
        Assert.DoesNotContain(Requests, request => request.Uri.AbsolutePath == "/UserViews");
    }

    [Fact]
    public async Task The_same_address_is_not_read_before_the_login()
    {
        var id = await AddAsync("1");

        await Controller().CheckServerEdit(id, Edit(password: "new-pass"), CancellationToken.None);

        Assert.Equal("/Users/AuthenticateByName", Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task An_administrator_login_of_a_check_is_ended()
    {
        var id = await AddAsync("1");
        _admin = true;

        var result = await Controller().CheckServerEdit(id, Edit(user: "admin"), CancellationToken.None);

        Assert.Equal(RemoteEditField.Username, Refusal(result).Failure.Field);
        var logout = Assert.Single(Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
        Assert.Equal($"jellymesh-{id:N}-check", DeviceOf(logout));
    }

    [Fact]
    public async Task An_administrator_login_of_a_save_is_ended()
    {
        var id = await AddAsync("1");
        _admin = true;

        var result = await Controller().SaveServerEdit(id, Edit(user: "admin"), CancellationToken.None);

        Assert.Equal(RemoteFailure.AdministratorAccount, Refusal(result).Failure.Failure);
        Assert.Single(Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
    }

    [Fact]
    public async Task A_server_saved_without_its_id_is_refused_when_another_configured_server_answers()
    {
        var id = await AddAsync("1");
        _serverId = OtherId;
        var other = await _fixture.Registry.AddAsync("Neighbour", OtherUrl, TestValues.Credentials, CancellationToken.None);
        Assert.True(other.IsSuccess);
        _fixture.Store.Configuration.Servers.Single(server => server.Id == id).JellyfinServerId = string.Empty;

        var result = await Controller().CheckServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        var (status, failure) = Refusal(result);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal(RemoteFailure.AlreadyConfigured, failure.Failure);
        Assert.Equal("Neighbour", failure.ServerName);
        Assert.Equal(RemoteEditField.Url, failure.Field);
    }

    [Fact]
    public async Task A_check_with_nothing_changed_has_no_consequence()
    {
        var id = await AddAsync("1", "2");

        var result = await Controller().CheckServerEdit(id, Edit(libraries: ["2", "1"]), CancellationToken.None);

        var consequences = Assert.IsType<ServerEditCheck>(result.Value).Consequences;
        Assert.False(consequences.Any);
        Assert.Null(consequences.Rename);
        Assert.Empty(consequences.RemovedLibraries);
        Assert.Equal(0, consequences.StoppedStreams);
    }

    [Fact]
    public async Task A_new_name_is_a_rename_with_the_libraries_it_renames()
    {
        var id = await AddAsync("1");

        var result = await Controller().CheckServerEdit(id, Edit(name: "  Casa  "), CancellationToken.None);

        var rename = Assert.IsType<ServerEditCheck>(result.Value).Consequences.Rename;
        Assert.NotNull(rename);
        Assert.Equal(("Friend", "Casa"), (rename.OldName, rename.NewName));
        Assert.Equal(["Friend - Film"], rename.Libraries);
    }

    [Fact]
    public async Task Libraries_left_out_or_no_longer_visible_are_removed()
    {
        var id = await AddAsync("1", "2", "9");

        var result = await Controller().CheckServerEdit(id, Edit(libraries: ["1", "9"]), CancellationToken.None);

        var check = Assert.IsType<ServerEditCheck>(result.Value);
        Assert.Equal(
            [new ImportedLibraryRemoval("2", "Serie", false), new ImportedLibraryRemoval("9", null, true)],
            check.Consequences.RemovedLibraries);
        Assert.Equal(["1"], check.Libraries.Where(library => library.Selected).Select(library => library.Id));
    }

    [Fact]
    public async Task A_new_user_stops_the_streams_of_the_server_and_a_new_url_does_not()
    {
        var id = await AddAsync("1");
        _playback.Sessions.Add(new PlaybackSession(PlaybackValues.Identity(), new StubSource(id, "item", "ms"), "play", PlaybackStreamKind.Hls, "/x", DateTimeOffset.UtcNow));
        _playback.Sessions.Add(PlaybackValues.Session());

        var user = await Controller().CheckServerEdit(id, Edit(user: "someone"), CancellationToken.None);
        var url = await Controller().CheckServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        Assert.Equal(1, Assert.IsType<ServerEditCheck>(user.Value).Consequences.StoppedStreams);
        Assert.Equal(0, Assert.IsType<ServerEditCheck>(url.Value).Consequences.StoppedStreams);
    }

    [Fact]
    public async Task The_answers_of_a_check_hold_no_token_and_no_password()
    {
        var id = await AddAsync("1");

        var accepted = await Controller().CheckServerEdit(id, Edit(password: "new-pass-sentinel"), CancellationToken.None);
        _serverId = OtherId;
        var refused = await Controller().CheckServerEdit(id, Edit(password: "new-pass-sentinel"), CancellationToken.None);

        foreach (var json in new[] { ToJson(accepted.Value), ToJson(((ObjectResult)refused.Result!).Value) })
        {
            Assert.DoesNotContain(TestValues.Token, json, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Password, json, StringComparison.Ordinal);
            Assert.DoesNotContain("new-pass-sentinel", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_answers_of_a_save_hold_no_token_and_no_password()
    {
        var id = await AddAsync("1");

        var saved = await Controller().SaveServerEdit(id, Edit(password: "new-pass-sentinel"), CancellationToken.None);
        _serverId = OtherId;
        var refused = await Controller().SaveServerEdit(id, Edit(password: "other-pass-sentinel"), CancellationToken.None);

        foreach (var json in new[] { ToJson(saved.Value), ToJson(((ObjectResult)refused.Result!).Value) })
        {
            Assert.DoesNotContain(TestValues.Token, json, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Password, json, StringComparison.Ordinal);
            Assert.DoesNotContain("pass-sentinel", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Empty_fields_reach_the_registry_and_are_refused_on_their_field()
    {
        var id = await AddAsync("1");
        var request = JsonSerializer.Deserialize<ServerEditRequest>("{\"Name\":\"\",\"Url\":\"\",\"Username\":\"\"}", JsonDefaults.Options)!;

        var result = await Controller().CheckServerEdit(id, request, CancellationToken.None);

        Assert.Equal(RemoteEditField.Name, Refusal(result).Failure.Field);
        Assert.DoesNotContain(typeof(ServerEditRequest).GetProperties(), property => property.IsDefined(typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), false));
    }

    [Fact]
    public async Task A_refused_login_is_shown_on_the_password()
    {
        var id = await AddAsync("1");
        _login = HttpStatusCode.Unauthorized;

        var result = await Controller().CheckServerEdit(id, Edit(password: "wrong"), CancellationToken.None);

        var (status, failure) = Refusal(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status);
        Assert.Equal(RemoteFailure.AuthenticationFailed, failure.Failure);
        Assert.Equal(RemoteEditField.Password, failure.Field);
    }

    [Theory]
    [InlineData(" ", TestValues.Url, TestValues.Username, RemoteEditField.Name)]
    [InlineData("Friend", "ftp://remote.invalid", TestValues.Username, RemoteEditField.Url)]
    [InlineData("Friend", TestValues.Url, "", RemoteEditField.Username)]
    public async Task An_invalid_field_is_refused_on_that_field_before_any_request(string name, string url, string user, RemoteEditField field)
    {
        var id = await AddAsync("1");

        var result = await Controller().CheckServerEdit(id, Edit(name: name, url: url, user: user), CancellationToken.None);

        var (status, failure) = Refusal(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(field, failure.Field);
        Assert.Empty(Requests);
    }

    [Fact]
    public async Task The_password_is_required_when_the_saved_one_cannot_be_read()
    {
        var id = await AddAsync("1");
        _fixture.Store.Configuration.Servers.Single().EncryptedCredentials = _fixture.Vault.Protect(Guid.NewGuid(), TestValues.Credentials)!;

        var empty = await Controller().CheckServerEdit(id, Edit(), CancellationToken.None);
        var entered = await Controller().CheckServerEdit(id, Edit(password: TestValues.Password), CancellationToken.None);

        var (status, failure) = Refusal(empty);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(RemoteEditField.Password, failure.Field);
        Assert.Equal("Enter the password of the dedicated user: the saved one can no longer be read.", failure.Message);
        Assert.IsType<ServerEditCheck>(entered.Value);
    }

    [Fact]
    public async Task An_unknown_server_is_not_found()
    {
        var result = await Controller().CheckServerEdit(Guid.NewGuid(), Edit(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, Refusal(result).Status);
    }

    [Fact]
    public async Task A_save_writes_every_field_in_one_save()
    {
        var id = await AddAsync("1");
        var saves = _fixture.Store.Saves;

        var result = await Controller().SaveServerEdit(id, Edit(name: " Casa ", url: OtherUrl, user: "someone", password: "new-pass", libraries: ["2"]), CancellationToken.None);

        var status = Assert.IsType<ServerStatus>(result.Value);
        Assert.Equal("Casa", status.Name);
        Assert.Equal(saves + 1, _fixture.Store.Saves);
        var server = _fixture.Store.Configuration.Servers.Single();
        Assert.Equal(("Casa", OtherUrl, FirstId), (server.Name, server.Url, server.JellyfinServerId));
        Assert.Equal(["2"], server.ImportedLibraryIds);
        var credentials = _fixture.Vault.Unprotect(id, server.EncryptedCredentials);
        Assert.Equal(("someone", "new-pass"), (credentials!.Username, credentials.Password));
    }

    [Fact]
    public async Task A_failed_save_puts_every_field_back_and_ends_the_new_login()
    {
        var id = await AddAsync("1");
        var before = Snapshot();
        _fixture.Store.SaveFailure = new InvalidOperationException("disk full");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Controller().SaveServerEdit(id, Edit(name: "Casa", url: OtherUrl, user: "someone", password: "new-pass", libraries: ["2"]), CancellationToken.None));

        Assert.Equal(before, Snapshot());
        var login = Requests.Last(request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
        Assert.Contains(Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout" && DeviceOf(request) == DeviceOf(login));
    }

    [Fact]
    public async Task A_failed_save_of_the_server_id_takes_it_back_out()
    {
        var id = await AddAsync("1");
        _fixture.Store.Configuration.Servers.Single().JellyfinServerId = string.Empty;
        _fixture.Store.SaveFailure = new InvalidOperationException("disk full");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller().SaveServerEdit(id, Edit(url: OtherUrl), CancellationToken.None));

        Assert.Empty(_fixture.Store.Configuration.Servers.Single().JellyfinServerId);
    }

    [Fact]
    public async Task A_server_saved_without_its_id_takes_the_one_read()
    {
        var id = await AddAsync("1");
        _fixture.Store.Configuration.Servers.Single().JellyfinServerId = string.Empty;

        var result = await Controller().SaveServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        Assert.IsType<ServerStatus>(result.Value);
        Assert.Equal(FirstId, _fixture.Store.Configuration.Servers.Single().JellyfinServerId);
    }

    [Fact]
    public async Task A_save_refuses_another_server_and_saves_nothing()
    {
        var id = await AddAsync("1");
        var before = Snapshot();
        var saves = _fixture.Store.Saves;
        _serverId = OtherId;

        var result = await Controller().SaveServerEdit(id, Edit(name: "Casa", url: OtherUrl, password: "new-pass"), CancellationToken.None);

        var (status, failure) = Refusal(result);
        Assert.Equal(StatusCodes.Status409Conflict, status);
        Assert.Equal(RemoteFailure.OtherServer, failure.Failure);
        Assert.Equal("Stranger box", failure.ServerName);
        Assert.Equal(saves, _fixture.Store.Saves);
        Assert.Equal(before, Snapshot());
        Assert.DoesNotContain(Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task A_new_name_alone_is_saved_with_no_remote()
    {
        var id = await AddAsync("1");
        _login = HttpStatusCode.ServiceUnavailable;

        var result = await Controller().SaveServerEdit(id, Edit(name: "Casa"), CancellationToken.None);

        Assert.Equal("Casa", Assert.IsType<ServerStatus>(result.Value).Name);
        Assert.Empty(Requests);
    }

    [Fact]
    public async Task An_unreachable_remote_blocks_a_new_address()
    {
        var id = await AddAsync("1");
        var before = Snapshot();
        _login = HttpStatusCode.ServiceUnavailable;

        var result = await Controller().SaveServerEdit(id, Edit(name: "Casa", url: OtherUrl), CancellationToken.None);

        Assert.Equal(RemoteEditField.Url, Refusal(result).Failure.Field);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task A_new_address_keeps_the_kept_login()
    {
        var id = await AddAsync("1");
        await _fixture.Registry.RunWithSessionAsync(id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);
        _fixture.Handler.Requests.Clear();

        await Controller().SaveServerEdit(id, Edit(url: OtherUrl), CancellationToken.None);

        Assert.All(Requests.Where(request => request.Uri.AbsolutePath is "/Users/AuthenticateByName" or "/Sessions/Logout"), request => Assert.Equal($"jellymesh-{id:N}-check", DeviceOf(request)));
        _fixture.Handler.Requests.Clear();
        var host = await _fixture.Registry.RunWithSessionAsync(id, (endpoint, _, _) => Task.FromResult(RemoteResult.Success(endpoint.BaseUrl.Host)), CancellationToken.None);
        Assert.Equal("other.invalid", host.Value);
        Assert.DoesNotContain(Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task A_new_password_logs_in_on_the_kept_device_and_keeps_that_login()
    {
        var id = await AddAsync("1");

        await Controller().SaveServerEdit(id, Edit(password: "new-pass"), CancellationToken.None);

        var login = Assert.Single(Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
        Assert.Equal($"jellymesh-{id:N}", DeviceOf(login));
        Assert.DoesNotContain(Requests, request => request.Uri.AbsolutePath == "/Sessions/Logout");
        _fixture.Handler.Requests.Clear();
        await _fixture.Registry.RunWithSessionAsync(id, (_, _, _) => Task.FromResult(RemoteResult.Success(1)), CancellationToken.None);
        Assert.DoesNotContain(Requests, request => request.Uri.AbsolutePath == "/Users/AuthenticateByName");
    }

    [Fact]
    public async Task Libraries_the_user_no_longer_sees_leave_the_imported_ones()
    {
        var id = await AddAsync("1", "2", "9");

        await Controller().SaveServerEdit(id, Edit(libraries: ["1", "9"]), CancellationToken.None);

        Assert.Equal(["1"], _fixture.Store.Configuration.Servers.Single().ImportedLibraryIds);
    }
}
