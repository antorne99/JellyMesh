using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Restores the numbers of an unlocked episode stub at the end of a refresh. "Replace all metadata" merges a blank item
/// over the episode after the pre-refresh group, which clears them and breaks NextUp, so <see cref="StubBeforeProbeProvider"/>
/// comes too early. A locked stub never gets this far, and it never loses them.
/// </summary>
public sealed class StubEpisodeNumbersProvider : ICustomMetadataProvider<Episode>, IForcedProvider, IHasOrder
{
    private readonly IStubRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubEpisodeNumbersProvider"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    public StubEpisodeNumbersProvider(IStubRegistry registry)
    {
        _registry = registry;
    }

    /// <inheritdoc />
    public string Name => "JellyMesh stub episode numbers";

    /// <inheritdoc />
    public int Order => 1000;

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var changed = _registry.FindByPath(item.Path) is { } stub && StubTechnicalData.WriteNumbers(item, stub);
        return Task.FromResult(changed ? ItemUpdateType.MetadataImport : ItemUpdateType.None);
    }
}
