using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.JellyMesh.Provider;

/// <summary>
/// Registers the provider role.
/// </summary>
public static class ProviderServices
{
    /// <summary>
    /// Adds the services of the ProviderApi.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void AddProvider(this IServiceCollection services)
    {
        services.AddSingleton<PeerDirectory>();
        services.AddSingleton<ProviderCatalog>();
        services.AddSingleton<ChangeJournal>();
        services.AddSingleton<StreamSlots>();
        services.AddSingleton<AbandonedStreamCloser>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<AbandonedStreamCloser>());
        services.AddSingleton<PeerGuardFilter>();
        services.AddSingleton<PeerPlaybackInfoFilter>();
        services.AddHostedService<ChangeFeedRecorder>();

        services.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<PeerGuardFilter>();
            options.Filters.AddService<PeerPlaybackInfoFilter>();
        });
    }
}
