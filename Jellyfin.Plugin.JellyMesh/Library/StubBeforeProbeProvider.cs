using System;
using System.Threading;
using Jellyfin.Plugin.JellyMesh.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// Runs before <c>ProbeProvider</c> (order 100): stops the probe and writes the data it would have found. The options
/// object is shared with the providers that follow, and that is what the mechanism rests on (Phase 0, point 3).
/// </summary>
public sealed class StubBeforeProbeProvider : StubMetadataProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StubBeforeProbeProvider"/> class.
    /// </summary>
    /// <param name="registry">The stub registry.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="technical">The technical data of the stubs.</param>
    public StubBeforeProbeProvider(IStubRegistry registry, IPluginConfigurationStore store, StubTechnicalData technical)
        : base(registry, store, technical)
    {
    }

    /// <inheritdoc />
    public override string Name => "JellyMesh stub data";

    /// <inheritdoc />
    public override int Order => 1;

    /// <inheritdoc />
    protected override ItemUpdateType Run(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsInFederatedFolder(item))
        {
            return ItemUpdateType.None;
        }

        options.EnableRemoteContentProbe = false;
        return Registry.FindByPath(item.Path) is { } stub && Technical.WriteItemAndStreams(item, stub, cancellationToken)
            ? ItemUpdateType.MetadataImport
            : ItemUpdateType.None;
    }
}
