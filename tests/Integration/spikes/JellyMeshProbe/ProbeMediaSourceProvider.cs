using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace JellyMesh.Probe;

/// <summary>Mode "source": the plain extension point, nothing else. Mode "sameid" reuses the item Id.</summary>
public sealed class ProbeMediaSourceProvider : IMediaSourceProvider
{
    private readonly ILogger<ProbeMediaSourceProvider> _logger;

    public ProbeMediaSourceProvider(ILogger<ProbeMediaSourceProvider> logger)
    {
        _logger = logger;
    }

    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (!Catalog.Has("source") || !Catalog.IsFederated(item))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        _logger.LogInformation("JMPROBE IMediaSourceProvider.GetMediaSources: {Path}", item.Path);
        var id = Catalog.Has("sameid")
            ? item.Id.ToString("N", CultureInfo.InvariantCulture)
            : "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        return Task.FromResult<IEnumerable<MediaSourceInfo>>([Catalog.Source(id, Catalog.DynamicUrl, Catalog.Has("external"))]);
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("The probe source never needs opening.");
    }
}
