using System;
using Jellyfin.Plugin.JellyMesh.Remote;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
        services.AddSingleton<ILastServedSourceStore>(provider => new LastServedSourceStore(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized."),
            provider.GetRequiredService<ILogger<LastServedSourceStore>>()));
        services.AddSingleton<PlaybackCoordinator>();
        services.AddSingleton<PlaybackRefusal>();
        services.AddSingleton<StubLookup>();
        services.AddSingleton<LocalCopyLookup>();
        services.AddSingleton<PlaybackVersions>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<StockRouteGuardFilter>();
        services.AddHostedService<PlaybackSweeper>();

        services.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<PlaybackInfoFilter>();
            options.Filters.AddService<StockRouteGuardFilter>();
        });
    }
}
