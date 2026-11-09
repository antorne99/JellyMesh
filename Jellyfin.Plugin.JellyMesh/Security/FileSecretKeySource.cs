using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyMesh.Security;

/// <summary>
/// Reads the credential key from a random file in the plugin data folder, made at the first start (decision 42).
/// </summary>
/// <remarks>
/// On Linux and macOS only the owner can read the file. On Windows it takes the permissions of the plugin data folder. If it
/// cannot be kept there is no key at all, never one for this run only: credentials saved with a key that dies at restart
/// could not be read again.
/// </remarks>
public sealed class FileSecretKeySource : ISecretKeySource
{
    /// <summary>The file name of the key.</summary>
    public const string FileName = "credential.key";

    private const int KeyBytes = 32;

    private readonly string? _key;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSecretKeySource"/> class.
    /// </summary>
    /// <param name="folder">The folder of the key.</param>
    /// <param name="logger">The logger.</param>
    public FileSecretKeySource(string folder, ILogger<FileSecretKeySource> logger)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(logger);
        _key = LoadOrCreate(Path.Combine(folder, FileName), logger);
    }

    /// <inheritdoc />
    public string? GetSecretKey() => _key;

    private static string? LoadOrCreate(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                // A key put back by hand may end with a newline.
                var saved = File.ReadAllText(path).Trim();
                if (IsKey(saved))
                {
                    KeepPrivate(path);
                    return saved;
                }

                // Moved aside rather than overwritten, so that a key damaged by hand can still be recovered.
                File.Move(path, path + ".damaged", overwrite: true);
                logger.LogWarning("The credential key file is damaged and was moved aside. A new one is made, and the password of each server must be entered again");
            }

            var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(KeyBytes));
            Save(path, key);
            return key;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Cannot keep the credential key file: {Error}. Sync and remote playback stay suspended", exception.GetType().Name);
            return null;
        }
    }

    private static bool IsKey(string value)
    {
        if (value.Length != KeyBytes * 2)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigitLower(character) && !char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    // A file restored from a backup may have come back readable by others.
    private static void KeepPrivate(string path)
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != Private)
        {
            File.SetUnixFileMode(path, Private);
        }
    }

    // A move, so that a crash never leaves half a key behind. The temporary file is always new, so it gets the private mode.
    private static void Save(string path, string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.Delete(temporary);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(key);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
