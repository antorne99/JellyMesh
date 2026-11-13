using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// At the start of the server, finishes the library renames a stop left halfway, so that no user keeps an old id
/// (§10 row 290).
/// </summary>
public sealed class LibraryRenameResumeService : BackgroundService
{
    private readonly IServerLibraryManager _libraries;
    private readonly ILogger<LibraryRenameResumeService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRenameResumeService"/> class.
    /// </summary>
    /// <param name="libraries">The automatic libraries, whose lock the renames share.</param>
    /// <param name="logger">The logger.</param>
    public LibraryRenameResumeService(IServerLibraryManager libraries, ILogger<LibraryRenameResumeService> logger)
    {
        _libraries = libraries;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _libraries.ResumeRenamesAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // What escapes a hosted service stops the whole server; the next rename tries again.
            _logger.LogError("Cannot finish the library renames left halfway: {Error}", exception.GetType().Name);
        }
    }
}
