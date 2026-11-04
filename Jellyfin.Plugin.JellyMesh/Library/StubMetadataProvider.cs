using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// What the two stub providers share. Pre-refresh and forced is what keeps them running on a locked item: Jellyfin leaves
/// the refresh right after the pre-refresh group and drops any provider that is not forced (Phase 0, point 3).
/// </summary>
public abstract class StubMetadataProvider :
    ICustomMetadataProvider<Movie>,
    ICustomMetadataProvider<Episode>,
    IHasOrder,
    IForcedProvider,
    IPreRefreshProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StubMetadataProvider"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="technical">The technical data of the stubs.</param>
    protected StubMetadataProvider(IStubRegistry registry, IPluginConfigurationStore store, StubTechnicalData technical)
    {
        Registry = registry;
        Store = store;
        Technical = technical;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract int Order { get; }

    /// <summary>Gets the stub registry.</summary>
    protected IStubRegistry Registry { get; }

    /// <summary>Gets the configuration store.</summary>
    protected IPluginConfigurationStore Store { get; }

    /// <summary>Gets the technical data of the stubs.</summary>
    protected StubTechnicalData Technical { get; }

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => Task.FromResult(Run(item, options, cancellationToken));

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => Task.FromResult(Run(item, options, cancellationToken));

    /// <summary>
    /// Handles a refresh of a file in the federated folder.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="options">The refresh options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What changed.</returns>
    protected abstract ItemUpdateType Run(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Tells whether an item is a <c>.strm</c> in the federated folder, even one the registry does not know yet.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether it is.</returns>
    protected bool IsInFederatedFolder(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Path is { } path
            && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            && FederatedPaths.RelativeTo(Store.Read(config => config.FederatedFolderPath), path) is not null;
    }
}
