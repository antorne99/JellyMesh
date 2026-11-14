using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyMesh.Segments;

/// <summary>
/// Runs after every library scan, by which the refresh of each new stub has read its <c>.strm</c>, and forgets the new
/// stubs that never got there, so they do not fill the list of new stubs (Q311).
/// </summary>
public sealed class NewStubScanEnd : ILibraryPostScanTask
{
    private readonly NewStubSegments _segments;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewStubScanEnd"/> class.
    /// </summary>
    /// <param name="segments">The segments of the new stubs.</param>
    public NewStubScanEnd(NewStubSegments segments)
    {
        _segments = segments;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _segments.ForgetUnread();
        return Task.CompletedTask;
    }
}
