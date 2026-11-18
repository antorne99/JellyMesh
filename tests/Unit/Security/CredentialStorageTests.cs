using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Storage;
using Jellyfin.Plugin.JellyMesh.Tests.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests.Security;

/// <summary>
/// Credentials in the plugin database, encrypted with the key file of the data folder, which never enters the database (§10 row 429).
/// </summary>
public sealed class CredentialStorageTests : IDisposable
{
    private const string LogoutPath = "/Sessions/Logout";

    private readonly LoginHandler _handler = new();
    private TestDatabase _database;

    public CredentialStorageTests()
    {
        Directory.CreateDirectory(Folder);
        _database = new TestDatabase(Folder);
    }

    // Under the temp folder, not the bind mount: WAL needs shared memory a host mount may not give.
    private string Folder { get; } = Path.Combine(Path.GetTempPath(), "jellymesh-credentials-" + Guid.NewGuid().ToString("N"));

    private string KeyPath => Path.Combine(Folder, FileSecretKeySource.FileName);

    public void Dispose()
    {
        _handler.Dispose();
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(Folder, true);
    }

    [Fact]
    public async Task ACopyOfTheDatabaseShowsTheServersButNoLoginAndNoKey()
    {
        using var plugin = Start();
        var server = await plugin.AddAsync();

        var copy = await _database.BackUpAsync(CancellationToken.None);

        var key = await File.ReadAllTextAsync(KeyPath);
        foreach (var text in new[] { DatabaseText(), ReadText(copy.FilePath) })
        {
            Assert.Contains("Friend", text, StringComparison.Ordinal);
            Assert.Contains(TestValues.Url, text, StringComparison.Ordinal);
            Assert.Contains(server.Id.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Username, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(TestValues.Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheServersColumnHoldsTheLoginEncryptedForThatServerOnly()
    {
        using var plugin = Start();
        var server = await plugin.AddAsync();

        var stored = await StoredCiphertextAsync(server.Id);

        var key = await File.ReadAllTextAsync(KeyPath);
        var payload = Encoding.UTF8.GetString(CredentialCipher.Decrypt(key, stored, server.Id.ToByteArray()));
        Assert.Contains(TestValues.Username, payload, StringComparison.Ordinal);
        Assert.Contains(TestValues.Password, payload, StringComparison.Ordinal);
        Assert.Throws<AuthenticationTagMismatchException>(() => CredentialCipher.Decrypt(key, stored, Guid.NewGuid().ToByteArray()));
    }

    [Fact]
    public async Task ANewPasswordOrARemovalLeavesNoOldCiphertextInTheDatabaseFiles()
    {
        using var plugin = Start();
        var server = await plugin.AddAsync();
        var first = await StoredCiphertextAsync(server.Id);

        var changed = await plugin.Registry.UpdateCredentialsAsync(server.Id, new RemoteCredentials { Username = TestValues.Username, Password = "n3w-pa55-sentinel" }, CancellationToken.None);
        Assert.True(changed.IsSuccess, changed.Failure.ToString());
        var second = await StoredCiphertextAsync(server.Id);

        Assert.NotEqual(first, second);
        Assert.DoesNotContain(first, DatabaseText(), StringComparison.Ordinal);

        Assert.True(await plugin.Registry.RemoveAsync(server.Id));

        Assert.DoesNotContain(second, DatabaseText(), StringComparison.Ordinal);
    }

    // §10 row 419 over the database: the new login is ended, and memory and database keep the saved credentials.
    [Fact]
    public async Task ANewPasswordWhoseSaveFailsEndsItsLoginAndKeepsTheSavedOne()
    {
        using var plugin = Start();
        var server = await plugin.AddAsync();
        var stored = await StoredCiphertextAsync(server.Id);
        _database.InterruptNextWrite();

        await Assert.ThrowsAsync<SqliteException>(() => plugin.Registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None));

        Assert.Equal([TestValues.Token + "2"], _handler.LoggedOut);
        Assert.Equal(stored, await StoredCiphertextAsync(server.Id));
        Assert.Equal(stored, plugin.Store.Read(config => config.Servers.Single().EncryptedCredentials));
        var used = await plugin.Registry.RunWithSessionAsync(server.Id, (_, session, _) => Task.FromResult(RemoteResult.Success(session.AccessToken)), CancellationToken.None);
        Assert.Equal(TestValues.Token + "1", used.Value);
    }

    // Rotation in 0.6: the key file is deleted, a restart makes a new one, and each server asks for its password.
    [Fact]
    public async Task DeletingTheKeyFileMakesANewKeyAtRestartAndTheServersAskForTheirPassword()
    {
        RemoteServerInfo server;
        string oldKey;
        using (var plugin = Start())
        {
            server = await plugin.AddAsync();
            oldKey = await File.ReadAllTextAsync(KeyPath);
        }

        File.Delete(KeyPath);
        Restart();
        using var restarted = Start();

        Assert.NotEqual(oldKey, await File.ReadAllTextAsync(KeyPath));
        Assert.Equal(KeyState.Wrong, restarted.Vault.GetKeyState());
        Assert.True(Assert.Single(restarted.Registry.GetServers()).NeedsPassword);

        var entered = await restarted.Registry.UpdateCredentialsAsync(server.Id, TestValues.Credentials, CancellationToken.None);

        Assert.True(entered.IsSuccess, entered.Failure.ToString());
        Assert.Equal(KeyState.Ok, restarted.Vault.GetKeyState());
        Assert.False(Assert.Single(restarted.Registry.GetServers()).NeedsPassword);
    }

    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.Latin1);
        return reader.ReadToEnd();
    }

    private Plugin Start()
    {
        var store = new PluginConfigurationStore(_database, new FixedFederatedRootSource(null), new CapturingLogger<PluginConfigurationStore>(), TimeProvider.System);
        var vault = new CredentialVault(new FileSecretKeySource(Folder, new CapturingLogger<FileSecretKeySource>()), store);
        var client = new JellyfinRemoteClient(new StubHttpClientFactory(_handler)) { Delay = (_, _) => Task.CompletedTask };
        return new Plugin(store, vault, new RemoteServerRegistry(store, vault, client, new CapturingLogger<RemoteServerRegistry>()));
    }

    private void Restart()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        _database = new TestDatabase(Folder);
    }

    private Task<string> StoredCiphertextAsync(Guid serverId) => _database.ReadAsync(
        (connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT encrypted_credentials FROM servers WHERE id = $id;";
            command.Parameters.AddWithValue("$id", serverId.ToString());
            return (string)command.ExecuteScalar()!;
        },
        CancellationToken.None);

    // The database file and its log as bytes on disk, which a copy of the data folder would hold.
    private string DatabaseText()
        => string.Concat(new[] { PluginDatabase.FileName, PluginDatabase.FileName + "-wal" }
            .Select(name => Path.Combine(Folder, name))
            .Where(File.Exists)
            .Select(ReadText));

    private sealed record Plugin(PluginConfigurationStore Store, CredentialVault Vault, RemoteServerRegistry Registry) : IDisposable
    {
        public async Task<RemoteServerInfo> AddAsync()
        {
            var result = await Registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Failure.ToString());
            return result.Value!;
        }

        public void Dispose() => Registry.Dispose();
    }

    // A stock remote whose logins each get their own token, recording the tokens logged out.
    private sealed class LoginHandler : HttpMessageHandler
    {
        private int _logins;

        public System.Collections.Generic.List<string> LoggedOut { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/AuthenticateByName")
            {
                var token = TestValues.Token + Interlocked.Increment(ref _logins);
                return Task.FromResult(StubHandler.Json(TestValues.LoginJson.Replace(TestValues.Token, token, StringComparison.Ordinal)));
            }

            if (path == LogoutPath)
            {
                var header = request.Headers.GetValues("Authorization").Single();
                var start = header.IndexOf("Token=\"", StringComparison.Ordinal) + "Token=\"".Length;
                lock (LoggedOut)
                {
                    LoggedOut.Add(header[start..header.IndexOf('"', start)]);
                }

                return Task.FromResult(StubHandler.Status(HttpStatusCode.NoContent));
            }

            return Task.FromResult(StubHandler.StockServer_Respond(request));
        }
    }
}
