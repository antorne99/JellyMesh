using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Runs after <c>PhotoProvider</c>, which reads the 1x1 placeholder of an unlocked photo, and writes the photo of the
/// index again (D45). A locked photo never reaches either: Jellyfin stops its refresh after the pre-refresh providers.
/// </summary>
public sealed class StubPhotoProvider : ICustomMetadataProvider<Photo>, IHasOrder, IForcedProvider
{
    private readonly IStubRegistry _registry;
    private readonly IPluginConfigurationStore _store;
    private readonly StubTechnicalData _technical;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubPhotoProvider"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="technical">The technical data of the stubs.</param>
    public StubPhotoProvider(IStubRegistry registry, IPluginConfigurationStore store, StubTechnicalData technical)
    {
        _registry = registry;
        _store = store;
        _technical = technical;
    }

    /// <inheritdoc />
    public string Name => "JellyMesh photo data";

    /// <inheritdoc />
    public int Order => 1000;

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Photo item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        // PhotoProvider points the Primary at the placeholder; the thumbnail takes it back.
        var update = StubMetadataProvider.IsPlaceholderInFederatedFolder(item, _store)
            && _store.ReadOr(config => config.FederatedFolderPath, null) is { } root
            && _registry.FindByPath(item.Path) is { Kind: StubKind.Photo } stub
                ? _technical.WritePhoto(item, stub, _store.ReadOr(config => config.MetadataProtection, true), root)
                : ItemUpdateType.None;
        return Task.FromResult(update);
    }
}
