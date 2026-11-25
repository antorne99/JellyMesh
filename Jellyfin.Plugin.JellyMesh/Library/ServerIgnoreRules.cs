using System;
using System.IO;
using Jellyfin.Plugin.JellyMesh.Remote;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Library;

/// <summary>
/// The ignore rules of the running server, asked through <see cref="ILibraryManager.IgnoreFile"/> about entries not yet written.
/// </summary>
public sealed class ServerIgnoreRules : IServerIgnoreRules
{
    private readonly ILibraryManager _library;
    private readonly string _root;
    private readonly ILogger _logger;
    private readonly ErrorLogLimits _errorLogs;

    /// <summary>Initializes a new instance of the <see cref="ServerIgnoreRules"/> class.</summary>
    /// <param name="library">The library manager.</param>
    /// <param name="root">The federated folder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="errorLogs">The limits of the errors in the log.</param>
    public ServerIgnoreRules(ILibraryManager library, string root, ILogger logger, ErrorLogLimits? errorLogs = null)
    {
        _errorLogs = errorLogs ?? new ErrorLogLimits();
        _library = library;
        _root = root;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Ignores(string folder, string name, bool isDirectory)
    {
        var parent = Path.Combine(_root, folder);
        return IsIgnored(Path.Combine(parent, name), isDirectory) && !IsIgnored(parent, true);
    }

    // Asks the server about a missing entry with no parent item; an error counts as not ignored.
    private bool IsIgnored(string path, bool isDirectory)
    {
        var entry = new FileSystemMetadata { FullName = path, Name = Path.GetFileName(path), IsDirectory = isDirectory, Exists = false };
        try
        {
            return _library.IgnoreFile(entry, null!);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug("The server could not tell whether it ignores {Path}: {Error}{Detail:l}", path, exception.GetType().Name, _logger.IsEnabled(LogLevel.Debug) ? _errorLogs.Local.Of(exception) : string.Empty);
            return false;
        }
    }
}
