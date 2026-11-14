using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyMesh.Segments;

/// <summary>
/// Gives the stubs the media segments of their best source. Jellyfin builds it before the plugin is loaded, so it
/// resolves its worker on first use.
/// </summary>
public sealed class StubSegmentProvider : IMediaSegmentProvider
{
    private readonly IServiceProvider _services;
    private StubSegments? _segments;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubSegmentProvider"/> class.
    /// </summary>
    /// <param name="services">The service provider.</param>
    public StubSegmentProvider(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public string Name => "JellyMesh";

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        return Segments() is { } segments
            ? segments.GetAsync(request, cancellationToken)
            : Task.FromResult<IReadOnlyList<MediaSegmentDto>>([]);
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return ValueTask.FromResult(Segments() is { } segments && segments.Supports(item.Id));
    }

    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        _segments?.Forget(itemId);
        return Task.CompletedTask;
    }

    // Null until the plugin is loaded, when the services of the worker can be built.
    private StubSegments? Segments()
    {
        if (_segments is null)
        {
            try
            {
                _segments = _services.GetRequiredService<StubSegments>();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        return _segments;
    }
}
