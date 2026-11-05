using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyMesh.Remote;

/// <summary>
/// The two HTTP clients of the plugin, the only ones it uses. Neither follows a redirect: a remote says where to go only
/// through <see cref="JellyfinRemoteClient"/>, which refuses other hosts. The default client logging is removed because
/// it records full request URLs, hence remote addresses.
/// </summary>
public static class RemoteHttpClients
{
    /// <summary>
    /// Adds the clients.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void AddRemoteHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient(JellyfinRemoteClient.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = JellyfinRemoteClient.RequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(CreateHandler)
            .RemoveAllLoggers();

        // A stream has no overall timeout: it lasts as long as the film. Callers bound the opening, and the relay a stall.
        services.AddHttpClient(RemotePlaybackClient.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(CreateHandler)
            .RemoveAllLoggers();
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = JellyfinRemoteClient.ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
        UseProxy = false
    };
}
