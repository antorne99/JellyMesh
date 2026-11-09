using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Dedup;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.JellyMesh.Tests;

internal static class TestValues
{
    public const string Key = "ZmFrZS10ZXN0LWtleS1ub3QtYS1yZWFsLXNlY3JldA==";
    public const string OtherKey = "b3RoZXItdGVzdC1rZXktbm90LWEtcmVhbC1zZWNyZXQ=";
    public const string Username = "dedicated-user";
    public const string Password = "pa55-w0rd-sentinel";
    public const string Token = "tok3n0123456789sentinel";
    public const string Host = "friend.tailnet.ts.net";
    public const string Url = "http://friend.tailnet.ts.net:8096";

    public static readonly RemoteCredentials Credentials = new() { Username = Username, Password = Password };

    public static readonly string LoginJson =
        "{\"AccessToken\":\"" + Token + "\",\"User\":{\"Id\":\"abc\",\"Policy\":{\"IsAdministrator\":false}}}";

    public const string ViewsJson =
        "{\"Items\":[{\"Id\":\"1\",\"Name\":\"Film\",\"CollectionType\":\"movies\"},"
        + "{\"Id\":\"2\",\"Name\":\"Serie\",\"CollectionType\":\"tvshows\"},"
        + "{\"Id\":\"3\",\"Name\":\"Musica\",\"CollectionType\":\"music\"}],\"TotalRecordCount\":3}";
}

internal sealed class InMemoryMergeStore : IManualMergeStore
{
    public IReadOnlyList<ManualMerge> Merges { get; private set; } = [];

    public bool IsDamaged { get; set; }

    public Task UpdateAsync(Func<IReadOnlyList<ManualMerge>, IReadOnlyList<ManualMerge>> change, CancellationToken cancellationToken)
    {
        Merges = change(Merges);
        return Task.CompletedTask;
    }
}

internal sealed class FixedFederatedRootSource(string? root) : IFederatedRootSource
{
    public string? GetFederatedRoot() => root;
}

internal sealed class InMemoryConfigurationStore : IPluginConfigurationStore
{
    public PluginConfiguration Configuration { get; } = new();

    public int Saves { get; private set; }

    public Exception? SaveFailure { get; set; }

    public event EventHandler? SettingsChanged;

    public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public T Read<T>(Func<PluginConfiguration, T> read) => read(Configuration);

    public void Update(Action<PluginConfiguration> update)
    {
        // Like the plugin, the change is made in memory before the save that may fail.
        update(Configuration);
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        Saves++;
    }
}

internal sealed class FixedLegacyKeySource : ILegacySecretKeySource
{
    public FixedLegacyKeySource(string? key) => Key = key;

    public string? Key { get; set; }

    public string? GetSecretKey() => Key;
}

internal sealed class FixedKeySource : ISecretKeySource
{
    public FixedKeySource(string? key) => Key = key;

    public string? Key { get; set; }

    public string? GetSecretKey() => Key;
}

/// <summary>Answers by path; records every request with its headers and body.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public List<RecordedRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    public static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    /// <summary>A stock server: login, views, and 404 on anything else.</summary>
    public static StubHandler StockServer() => new(StockServer_Respond);

    public static HttpResponseMessage StockServer_Respond(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/Users/AuthenticateByName" => Json(TestValues.LoginJson),
        "/UserViews" => Json(TestValues.ViewsJson),
        _ => Status(HttpStatusCode.NotFound)
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Accept.ToString(),
            request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null,
            body));
        return _respond(request);
    }

    internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Accept, string? Authorization, string? Body);
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Add(formatter(state, exception) + " " + exception);
}

/// <summary>Real vault, client and registry over stubs for the key, the config and the network.</summary>
internal sealed class RegistryFixture : IDisposable
{
    public RegistryFixture(StubHandler? handler = null, string? key = TestValues.Key)
    {
        Handler = handler ?? StubHandler.StockServer();
        KeySource = new FixedKeySource(key);
        Vault = new CredentialVault(KeySource, Store);
        Client = new JellyfinRemoteClient(new StubHttpClientFactory(Handler));
        Registry = new RemoteServerRegistry(Store, Vault, Client, Logger);
    }

    public StubHandler Handler { get; }

    public InMemoryConfigurationStore Store { get; } = new();

    public FixedKeySource KeySource { get; }

    public CredentialVault Vault { get; }

    public JellyfinRemoteClient Client { get; }

    public CapturingLogger<RemoteServerRegistry> Logger { get; } = new();

    public RemoteServerRegistry Registry { get; }

    public async Task<RemoteServerInfo> AddServerAsync()
    {
        var result = await Registry.AddAsync("Friend", TestValues.Url, TestValues.Credentials, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Failure.ToString());
        return result.Value!;
    }

    public void Dispose() => Registry.Dispose();
}
