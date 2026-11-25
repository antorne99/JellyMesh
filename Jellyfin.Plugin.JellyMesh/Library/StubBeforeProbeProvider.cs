using System;
using System.IO;
using System.Linq;
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

        // A book or a photo has no .nfo and nothing to probe: its fields come from the index (D62, D45).
        if (IsPlaceholderInFederatedFolder(item))
        {
            var isProtected = Store.ReadOr(config => config.MetadataProtection, true);
            return Registry.FindByPath(item.Path) switch
            {
                { Kind: StubKind.Book } book => Technical.WriteBook(item, book, isProtected) ? ItemUpdateType.MetadataImport : ItemUpdateType.None,
                { Kind: StubKind.Photo } photo when Store.ReadOr(config => config.FederatedFolderPath, null) is { } root => Technical.WritePhoto(item, photo, isProtected, root),
                _ => ItemUpdateType.None
            };
        }

        if (!IsInFederatedFolder(item))
        {
            return ItemUpdateType.None;
        }

        options.EnableRemoteContentProbe = false;
        if (item is AudioBook)
        {
            MakeShortcut(item);
        }

        if (Registry.FindByPath(item.Path) is not { } stub)
        {
            return ItemUpdateType.None;
        }

        var changed = Technical.WriteItemAndStreams(item, stub, cancellationToken);
        changed |= stub.Kind is StubKind.Track or StubKind.AudioBook && Technical.WriteTrack(item, stub, Store.ReadOr(config => config.MetadataProtection, true));
        return changed ? ItemUpdateType.MetadataImport : ItemUpdateType.None;
    }

    // 12.4 does not store IsShortcut on an AudioBook, so the probe would open the file each refresh (F48).
    // Nothing is reported as changed: saving the item would not keep the flag either.
    private static void MakeShortcut(BaseItem item)
    {
        item.IsShortcut = true;
        try
        {
            item.ShortcutPath = File.ReadLines(item.Path).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? item.ShortcutPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The probe is skipped all the same; the path stays as it was.
        }
    }
}
