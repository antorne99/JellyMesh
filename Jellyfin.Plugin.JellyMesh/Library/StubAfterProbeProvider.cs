using System.Threading;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Runs after <c>ProbeProvider</c>: restores what it undoes on an item it could not probe.
/// </summary>
public sealed class StubAfterProbeProvider : StubMetadataProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StubAfterProbeProvider"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="technical">The technical data of the stubs.</param>
    public StubAfterProbeProvider(IStubRegistry registry, IPluginConfigurationStore store, StubTechnicalData technical)
        : base(registry, store, technical)
    {
    }

    /// <inheritdoc />
    public override string Name => "JellyMesh stub streams";

    /// <inheritdoc />
    public override int Order => 1000;

    /// <inheritdoc />
    protected override ItemUpdateType Run(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (IsInFederatedFolder(item) && Registry.FindByPath(item.Path) is { } stub)
        {
            Technical.WriteStreamsAndChapters(item, stub, cancellationToken);
        }

        return ItemUpdateType.None;
    }
}
