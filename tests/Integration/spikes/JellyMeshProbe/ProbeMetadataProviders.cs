using System.Text.RegularExpressions;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace JellyMesh.Probe;

/// <summary>
/// Shared shape of both providers. IPreRefreshProvider and IForcedProvider are what
/// keep them running on a locked item: MetadataService returns right after the
/// pre-refresh group when IsLocked, and ProviderManager drops unforced providers.
/// </summary>
public abstract class ProbeMetadataProvider :
    ICustomMetadataProvider<Movie>,
    ICustomMetadataProvider<Episode>,
    IHasOrder,
    IForcedProvider,
    IPreRefreshProvider
{
    protected ProbeMetadataProvider(ILogger logger, IMediaStreamRepository streams)
    {
        Logger = logger;
        StreamRepository = streams;
    }

    public abstract string Name { get; }

    public abstract int Order { get; }

    protected ILogger Logger { get; }

    protected IMediaStreamRepository StreamRepository { get; }

    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        return Task.FromResult(Run(item, options, cancellationToken));
    }

    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        return Task.FromResult(Run(item, options, cancellationToken));
    }

    protected abstract ItemUpdateType Run(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken);
}

/// <summary>Runs before ProbeProvider (Order 100).</summary>
public sealed class ProbeBeforeProvider : ProbeMetadataProvider
{
    public ProbeBeforeProvider(ILogger<ProbeBeforeProvider> logger, IMediaStreamRepository streams)
        : base(logger, streams)
    {
    }

    public override string Name => "JellyMesh Probe Before";

    public override int Order => 1;

    protected override ItemUpdateType Run(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (!Catalog.IsFederated(item))
        {
            return ItemUpdateType.None;
        }

        Logger.LogInformation(
            "JMPROBE before: {Path} locked={Locked} remoteProbe={Probe} mode={Mode}",
            item.Path,
            item.IsLocked,
            options.EnableRemoteContentProbe,
            options.MetadataRefreshMode);

        var update = ItemUpdateType.None;

        // Point 6: the real flag, stored on the item, as opposed to rewriting the DTO.
        if (Catalog.Has("placeholder") && item is Video { IsPlaceHolder: false } video && Refusal.IsOffline(item))
        {
            video.IsPlaceHolder = true;
            update |= ItemUpdateType.MetadataEdit;
        }

        // Point 6: a real film length, because resume and watched rules depend on it.
        if (Catalog.Has("longrun") && Catalog.IsFederated(item))
        {
            options.EnableRemoteContentProbe = false;
            item.RunTimeTicks = TimeSpan.FromHours(2).Ticks;
            update = ItemUpdateType.MetadataImport;
        }

        if (Catalog.Has("noprobe"))
        {
            // The options object is shared with ProbeProvider, which reads it next.
            options.EnableRemoteContentProbe = false;
        }

        if (Catalog.Has("prefill"))
        {
            item.RunTimeTicks = Catalog.RunTimeTicks;
            item.TotalBitrate = Catalog.Bitrate;
            item.Container = Catalog.Container;
            item.Size = Catalog.Size;
            StreamRepository.SaveMediaStreams(item.Id, Catalog.Streams(Catalog.Has("external")), cancellationToken);
            update = ItemUpdateType.MetadataImport;
        }

        // Mode "epnumbers": a later-added episode of a locked series never gets its .nfo read.
        if (Catalog.Has("epnumbers") && item is Episode { IndexNumber: null } episode)
        {
            var match = Regex.Match(item.Path, @"S(\d+)E(\d+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (match.Success)
            {
                episode.ParentIndexNumber = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                episode.IndexNumber = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                update = ItemUpdateType.MetadataImport;
            }
        }

        return update;
    }
}

/// <summary>
/// Runs after ProbeProvider, whose Fetch drops external streams and renumbers the
/// rest. Still a pre-refresh provider, because a plain one is skipped on locked items.
/// </summary>
public sealed class ProbeAfterProvider : ProbeMetadataProvider
{
    private readonly IChapterManager _chapters;

    public ProbeAfterProvider(ILogger<ProbeAfterProvider> logger, IMediaStreamRepository streams, IChapterManager chapters)
        : base(logger, streams)
    {
        _chapters = chapters;
    }

    public override string Name => "JellyMesh Probe After";

    public override int Order => 1000;

    protected override ItemUpdateType Run(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (!Catalog.IsFederated(item))
        {
            return ItemUpdateType.None;
        }

        if (Catalog.Has("resave"))
        {
            Logger.LogInformation("JMPROBE after: {Path} resaving streams", item.Path);
            StreamRepository.SaveMediaStreams(item.Id, Catalog.Streams(Catalog.Has("external")), cancellationToken);
        }

        // ProbeProvider stores an empty chapter list whenever it runs without a probe result.
        if (Catalog.Has("chapters") && item is Video video)
        {
            _chapters.SaveChapters(video, Catalog.Chapters());
        }

        return ItemUpdateType.None;
    }
}
