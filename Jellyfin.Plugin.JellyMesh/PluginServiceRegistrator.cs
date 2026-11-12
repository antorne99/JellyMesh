using System;
using Jellyfin.Plugin.JellyMesh.Activity;
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
using Microsoft.AspNetCore.Mvc;
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
        serviceCollection.AddSingleton<IActivityWriter, JellyfinActivityWriter>();
        serviceCollection.AddSingleton<ISecretKeySource>(provider => new FileSecretKeySource(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized."),
            provider.GetRequiredService<ILogger<FileSecretKeySource>>()));
        serviceCollection.AddSingleton<IFederatedRootSource, EnvironmentFederatedRootSource>();
        serviceCollection.AddSingleton<ILegacySecretKeySource, EnvironmentSecretKeySource>();
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
        serviceCollection.AddSingleton<IManualMergeStore>(provider => new ManualMergeStore(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized."),
            provider.GetRequiredService<ILogger<ManualMergeStore>>()));
        serviceCollection.AddSingleton<ManualMergeService>();
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
        serviceCollection.AddSingleton<ILibraryScans, LibraryScans>();
        serviceCollection.AddSingleton<IServerLibraryManager, ServerLibraryManager>();
        serviceCollection.AddSingleton<RetiredLibraryIds>();
        serviceCollection.AddSingleton<ILibraryViewIds, LibraryViewIds>();
        serviceCollection.AddSingleton<ILibraryRenamer, LibraryRenamer>();
        serviceCollection.AddSingleton<RetiredLibraryFilter>();
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<RetiredLibraryFilter>());
        serviceCollection.AddSingleton<IVersionLinker, VersionLinker>();
        serviceCollection.AddSingleton<IWatchStateCarryLog>(provider => new WatchStateCarryLog(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized."),
            provider.GetRequiredService<ILogger<WatchStateCarryLog>>()));
        serviceCollection.AddSingleton<IWatchStateCarrier, WatchStateCarrier>();
        serviceCollection.AddSingleton<IServerStubRenamer, ServerStubRenamer>();
        serviceCollection.AddSingleton<IMergeWatchStateCarry, MergeWatchStateCarry>();
        serviceCollection.AddSingleton<ISyncHistory>(provider => new SyncHistoryStore(
            Plugin.Instance?.DataFolderPath ?? throw new InvalidOperationException("The JellyMesh plugin is not initialized."),
            provider.GetRequiredService<ILogger<SyncHistoryStore>>()));
        serviceCollection.AddSingleton<ILayoutMigration, LayoutMigration>();
        serviceCollection.AddSingleton<ICatalogSyncService, CatalogSyncService>();
        serviceCollection.AddHostedService<SyncScheduleService>();

        serviceCollection.AddRemoteHttpClients();
        serviceCollection.AddPlayback();
        serviceCollection.AddProvider();
    }
}
