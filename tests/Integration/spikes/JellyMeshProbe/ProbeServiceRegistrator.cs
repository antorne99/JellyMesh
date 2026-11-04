using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellyMesh.Probe;

/// <summary>Runs after the server registered its own services, so a replacement wins.</summary>
public sealed class ProbeServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ProbePlaybackInfoFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<ProbePlaybackInfoFilter>());

        serviceCollection.AddSingleton<ProbeRefusalFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<ProbeRefusalFilter>());

        if (!Catalog.Has("decorator"))
        {
            return;
        }

        var original = serviceCollection.Single(d => d.ServiceType == typeof(IMediaSourceManager));
        var implementation = original.ImplementationType
            ?? throw new InvalidOperationException("IMediaSourceManager is not registered by type");
        serviceCollection.Remove(original);
        serviceCollection.AddSingleton<IMediaSourceManager>(provider => new ProbeMediaSourceManager(
            (IMediaSourceManager)ActivatorUtilities.CreateInstance(provider, implementation),
            provider.GetRequiredService<ILogger<ProbeMediaSourceManager>>()));
    }
}
