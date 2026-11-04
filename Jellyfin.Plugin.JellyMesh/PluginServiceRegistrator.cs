using System;
using System.Net.Http;
using Jellyfin.Plugin.JellyMesh.Configuration;
using Jellyfin.Plugin.JellyMesh.Index;
using Jellyfin.Plugin.JellyMesh.Library;
using Jellyfin.Plugin.JellyMesh.Playback;
using Jellyfin.Plugin.JellyMesh.Provider;
using Jellyfin.Plugin.JellyMesh.Remote;
using Jellyfin.Plugin.JellyMesh.Security;
using Jellyfin.Plugin.JellyMesh.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IPluginConfigurationStore, PluginConfigurationStore>();
        serviceCollection.AddSingleton<ISecretKeySource, EnvironmentSecretKeySource>();
        serviceCollection.AddSingleton<ICredentialVault, CredentialVault>();
        serviceCollection.AddSingleton<IJellyfinRemoteClient, JellyfinRemoteClient>();
        serviceCollection.AddSingleton<IRemoteServerRegistry, RemoteServerRegistry>();
        serviceCollection.AddSingleton<IFederatedFolderLinkChecker, FederatedFolderLinkChecker>();

        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton(provider => new IndexFile(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized."),
            provider.GetRequiredService<ILogger<IndexFile>>()));
        serviceCollection.AddSingleton<IFederationIndex, FederationIndex>();
        serviceCollection.AddSingleton<ILocalCatalogReader, LocalCatalogReader>();
        serviceCollection.AddSingleton<IFederationResolver, FederationResolver>();
        serviceCollection.AddSingleton<IRemoteCatalogReader, RemoteCatalogReader>();
        serviceCollection.AddSingleton(provider => new StubRegistry(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized."),
            provider.GetRequiredService<IPluginConfigurationStore>(),
            provider.GetRequiredService<ILogger<StubRegistry>>()));
        serviceCollection.AddSingleton<IStubRegistry>(provider => provider.GetRequiredService<StubRegistry>());
        serviceCollection.AddSingleton(provider => new StrmSigner(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized."),
            provider.GetRequiredService<ILogger<StrmSigner>>()));
        serviceCollection.AddSingleton<StubUrlBuilder>();
        serviceCollection.AddSingleton<StubTechnicalData>();
        serviceCollection.AddSingleton<StubItemUpdater>();
        serviceCollection.AddSingleton<StubRemover>();
        serviceCollection.AddSingleton<ILibraryMaterializer, LibraryMaterializer>();
        serviceCollection.AddSingleton<ICatalogSyncService, CatalogSyncService>();
        serviceCollection.AddHostedService<SyncScheduleService>();

        // Redirects are handled by JellyfinRemoteClient, which refuses other hosts. The default
        // client logging is removed because it records full request URLs, hence remote addresses.
        serviceCollection.AddHttpClient(JellyfinRemoteClient.HttpClientName)
            .ConfigureHttpClient(client =>
            {
                client.Timeout = JellyfinRemoteClient.RequestTimeout;
                client.MaxResponseContentBufferSize = JellyfinRemoteClient.MaxResponseBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = JellyfinRemoteClient.ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseCookies = false,
                UseProxy = false
            })
            .RemoveAllLoggers();

        serviceCollection.AddPlayback();
        serviceCollection.AddProvider();
    }
}
