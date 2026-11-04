using System;
using System.Net.Http;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.JellyMesh.Playback;

/// <summary>
/// Registers playback.
/// </summary>
public static class PlaybackServices
{
    /// <summary>
    /// Adds the services of playback.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void AddPlayback(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IRemotePlaybackClient, RemotePlaybackClient>();
        services.AddSingleton<PlaybackSessionStore>();
        services.AddSingleton<PlaybackCoordinator>();
        services.AddSingleton<PlaybackRefusal>();
        services.AddSingleton<StubLookup>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<StockRouteGuardFilter>();
        services.AddHostedService<PlaybackSweeper>();

        services.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<PlaybackInfoFilter>();
            options.Filters.AddService<StockRouteGuardFilter>();
        });

        // A second client: a stream has no overall timeout, and the first one has a 30 s limit. No redirects, and the
        // default logging is removed because it records full request URLs, hence remote addresses.
        services.AddHttpClient(RemotePlaybackClient.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = JellyfinRemoteClient.ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseCookies = false,
                UseProxy = false
            })
            .RemoveAllLoggers();

        // Materialization registers the real registry. This holds until both are merged, and never replaces it.
        services.TryAddSingleton<IStubRegistry, NoStubs>();
    }

    private sealed class NoStubs : IStubRegistry
    {
        public StubEntry? Find(string stubId) => null;

        public StubEntry? FindByPath(string? path) => null;
    }
}
